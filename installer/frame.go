package main

// The ssh connection to the Frame, with the client of golang.org/x/crypto/ssh (no ssh program needed on the computer), and
// the pairing that gives it access.
//
// Pairing is what the SteamOS Devkit Client does, and what dist/install.sh does: the installer's own ssh key is POSTed to the
// devkit service that Developer Mode starts (http://<frame>:32000/register); Steam asks the wearer to accept the
// "development host", and on acceptance adds the key to the user's authorized keys and switches sshd on. The key is the one
// the install scripts make (~/.config/lemmix-frame/frame_rsa, %LOCALAPPDATA%\lemmix-frame\frame_rsa on Windows), so a
// computer paired by either is paired for both.
//
// Signing in tries the installer's key, the Devkit Client's key, the ssh agent and the user's own keys, then the password
// set in Developer Mode when one is typed. The Frame's host key is trusted the first time and remembered (settings.json); a
// changed key stops the connection until the user trusts the new one (a reset Frame has a new key).

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"time"

	"golang.org/x/crypto/ssh"
	"golang.org/x/crypto/ssh/agent"
)

const devkitPort = "32000"

// settings.json in the user's configuration folder: the last Frame used and the host keys of the Frames seen
type settings struct {
	LastAddress string            `json:"lastAddress,omitempty"`
	HostKeys    map[string]string `json:"hostKeys,omitempty"` // host name or address -> key type and base64, like known_hosts
}

var settingsLock sync.Mutex

func settingsPath() string {
	dir, err := os.UserConfigDir()
	if err != nil {
		return ""
	}
	return filepath.Join(dir, "lemmix-frame-installer", "settings.json")
}

func loadSettings() settings {
	settingsLock.Lock()
	defer settingsLock.Unlock()
	var s settings
	if data, err := os.ReadFile(settingsPath()); err == nil {
		json.Unmarshal(data, &s)
	}
	if s.HostKeys == nil {
		s.HostKeys = map[string]string{}
	}
	return s
}

func updateSettings(change func(*settings)) {
	s := loadSettings()
	change(&s)
	settingsLock.Lock()
	defer settingsLock.Unlock()
	path := settingsPath()
	if path == "" {
		return
	}
	os.MkdirAll(filepath.Dir(path), 0o700)
	data, _ := json.MarshalIndent(s, "", "  ")
	os.WriteFile(path, data, 0o600)
}

// keyPath: where the install scripts keep a key (their folders, not Go's configuration folder, so that the keys are shared)
func keyPath(folder, name string) string {
	if runtime.GOOS == "windows" {
		if dir := os.Getenv("LOCALAPPDATA"); dir != "" {
			return filepath.Join(dir, folder, name)
		}
		return ""
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return ""
	}
	return filepath.Join(home, ".config", folder, name)
}

func installerKeyPath() string { return keyPath("lemmix-frame", "frame_rsa") }
func devkitKeyPath() string    { return keyPath("steamos-devkit", "devkit_rsa") }

// installerKey loads the installer's key, making it the first time: RSA, the type the devkit service takes, in OpenSSH's
// format with its public half beside it (frame_rsa.pub), as ssh-keygen writes them for install.sh
func installerKey() (ssh.Signer, string, error) {
	path := installerKeyPath()
	if path == "" {
		return nil, "", errors.New("no folder for the installer's key")
	}
	if data, err := os.ReadFile(path); err == nil {
		signer, err := ssh.ParsePrivateKey(data)
		if err != nil {
			return nil, "", fmt.Errorf("%s cannot be read: %v", path, err)
		}
		public, err := os.ReadFile(path + ".pub")
		if err != nil || !strings.HasPrefix(string(public), "ssh-rsa ") {
			public = []byte(strings.TrimSpace(string(ssh.MarshalAuthorizedKey(signer.PublicKey()))) + " " + keyComment() + "\n")
			os.WriteFile(path+".pub", public, 0o644)
		}
		return signer, strings.TrimSpace(string(public)), nil
	}
	key, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		return nil, "", err
	}
	block, err := ssh.MarshalPrivateKey(key, keyComment())
	if err != nil {
		return nil, "", err
	}
	signer, err := ssh.NewSignerFromKey(key)
	if err != nil {
		return nil, "", err
	}
	public := strings.TrimSpace(string(ssh.MarshalAuthorizedKey(signer.PublicKey()))) + " " + keyComment()
	os.MkdirAll(filepath.Dir(path), 0o700)
	if err := os.WriteFile(path, pem.EncodeToMemory(block), 0o600); err != nil {
		return nil, "", err
	}
	os.WriteFile(path+".pub", []byte(public+"\n"), 0o644)
	logf("made the installer's key %s", path)
	return signer, public, nil
}

// keyComment names the key after this computer, as install.sh does: the headset shows it in its pairing request, and the
// README's "removing the pairing" finds it by its "lemmix-installer@" prefix
func keyComment() string {
	host, _ := os.Hostname()
	host, _, _ = strings.Cut(host, ".")
	host = regexp.MustCompile(`[^A-Za-z0-9._-]`).ReplaceAllString(host, "-")
	if host == "" {
		host = "computer"
	}
	return "lemmix-installer@" + host
}

// devkitGet reads a page of the Frame's devkit service (it answers only in Developer Mode)
func devkitGet(ip, page string, timeout time.Duration) (string, error) {
	client := &http.Client{Timeout: timeout}
	response, err := client.Get("http://" + net.JoinHostPort(ip, devkitPort) + "/" + page)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(response.Body, 64*1024))
	if response.StatusCode != 200 {
		return "", fmt.Errorf("%s: %s", response.Status, strings.TrimSpace(string(body)))
	}
	return string(body), nil
}

// errPairingRefused: the devkit service answered, but not with an accepted pairing (declined, or no answer in time)
type errPairingRefused struct{ message string }

func (e *errPairingRefused) Error() string { return e.message }

// requestPairing sends the installer's public key to the Frame, which asks the wearer to accept it; it returns once the
// Frame answered (it waits about 30 seconds for the wearer)
func requestPairing(ip, publicKey string) error {
	client := &http.Client{Timeout: 75 * time.Second}
	response, err := client.Post("http://"+net.JoinHostPort(ip, devkitPort)+"/register", "text/plain", strings.NewReader(publicKey+"\n"))
	if err != nil {
		return err
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(response.Body, 64*1024))
	logf("pairing answer: %s %s", response.Status, strings.TrimSpace(string(body)))
	if response.StatusCode != 200 {
		message := strings.TrimSpace(string(body))
		if message == "" {
			message = response.Status
		}
		return &errPairingRefused{message: message}
	}
	return nil
}

// errHostKeyChanged: the Frame does not have the key it had before
type errHostKeyChanged struct{ name, key string }

func (e *errHostKeyChanged) Error() string { return "the Frame's identity (ssh host key) has changed" }

func keyText(key ssh.PublicKey) string {
	return key.Type() + " " + base64.StdEncoding.EncodeToString(key.Marshal())
}

func hostKeyCallback(keyName string) ssh.HostKeyCallback {
	return func(_ string, _ net.Addr, key ssh.PublicKey) error {
		known, ok := loadSettings().HostKeys[keyName]
		text := keyText(key)
		if !ok {
			updateSettings(func(s *settings) { s.HostKeys[keyName] = text })
			return nil
		}
		if known != text {
			return &errHostKeyChanged{name: keyName, key: text}
		}
		return nil
	}
}

// authMethods: the installer's key and the Devkit Client's (pairing), the ssh agent and the user's unencrypted keys, then the
// password. SteamOS asks for it as "keyboard-interactive" (PAM), other systems as "password": both are offered.
func authMethods(password string) []ssh.AuthMethod {
	var methods []ssh.AuthMethod
	var signers []ssh.Signer
	for _, path := range []string{installerKeyPath(), devkitKeyPath()} {
		if data, err := os.ReadFile(path); path != "" && err == nil {
			if signer, err := ssh.ParsePrivateKey(data); err == nil {
				signers = append(signers, signer)
			}
		}
	}
	if socket := os.Getenv("SSH_AUTH_SOCK"); socket != "" {
		if conn, err := net.DialTimeout("unix", socket, 2*time.Second); err == nil {
			if agentSigners, err := agent.NewClient(conn).Signers(); err == nil {
				signers = append(signers, agentSigners...)
			}
		}
	}
	if home, err := os.UserHomeDir(); err == nil {
		for _, name := range []string{"id_ed25519", "id_ecdsa", "id_rsa"} {
			if data, err := os.ReadFile(filepath.Join(home, ".ssh", name)); err == nil {
				if signer, err := ssh.ParsePrivateKey(data); err == nil {
					signers = append(signers, signer)
				}
			}
		}
	}
	if len(signers) > 4 { // the server stops after 6 failed attempts: keep some for the password
		signers = signers[:4]
	}
	if len(signers) > 0 {
		methods = append(methods, ssh.PublicKeys(signers...))
	}
	if password != "" {
		methods = append(methods,
			ssh.KeyboardInteractive(func(_, _ string, questions []string, _ []bool) ([]string, error) {
				answers := make([]string, len(questions))
				for i := range answers {
					answers[i] = password
				}
				return answers, nil
			}),
			ssh.Password(password))
	}
	return methods
}

// frameConnection is the open ssh connection; keepalives notice a Frame that went to sleep
type frameConnection struct {
	client *ssh.Client
	lost   chan struct{}
}

func dialFrame(user, address, keyName, password string) (*frameConnection, error) {
	config := &ssh.ClientConfig{
		User:            user,
		Auth:            authMethods(password),
		HostKeyCallback: hostKeyCallback(keyName),
		Timeout:         15 * time.Second,
	}
	// Ask for the type of key already known, so that the Frame's other keys (it has several types) never look like a change
	if known, ok := loadSettings().HostKeys[keyName]; ok {
		if keyType, _, _ := strings.Cut(known, " "); keyType != ssh.KeyAlgoRSA {
			config.HostKeyAlgorithms = []string{keyType}
		}
	}
	client, err := ssh.Dial("tcp", net.JoinHostPort(address, "22"), config)
	if err != nil {
		return nil, err
	}
	conn := &frameConnection{client: client, lost: make(chan struct{})}
	go func() {
		defer close(conn.lost)
		for {
			result := make(chan error, 1)
			go func() { _, _, err := client.SendRequest("keepalive@openssh.com", true, nil); result <- err }()
			select {
			case err := <-result:
				if err != nil {
					client.Close()
					return
				}
			case <-time.After(20 * time.Second):
				client.Close() // no answer: the Frame is asleep or gone
				return
			}
			time.Sleep(10 * time.Second)
		}
	}()
	return conn, nil
}

func (c *frameConnection) close() { c.client.Close() }

func (c *frameConnection) alive() bool {
	select {
	case <-c.lost:
		return false
	default:
		return true
	}
}

// output runs a command and returns what it prints
func (c *frameConnection) output(command string, stdin io.Reader) (string, error) {
	session, err := c.client.NewSession()
	if err != nil {
		return "", err
	}
	defer session.Close()
	var out, errOut bytes.Buffer
	session.Stdout = &out
	session.Stderr = &errOut
	session.Stdin = stdin
	if err := session.Run(command); err != nil {
		message := strings.TrimSpace(errOut.String())
		if message == "" {
			message = err.Error()
		}
		return out.String(), errors.New(message)
	}
	return out.String(), nil
}

// send runs a command with data on its input, and returns once all of it is sent: remote is the command's error, local the
// error reading data
func (c *frameConnection) send(command string, data io.Reader) (remote, local error) {
	session, err := c.client.NewSession()
	if err != nil {
		return err, nil
	}
	defer session.Close()
	var errOut bytes.Buffer
	session.Stderr = &errOut
	stdin, err := session.StdinPipe()
	if err != nil {
		return err, nil
	}
	if err := session.Start(command); err != nil {
		return err, nil
	}
	copied := make(chan error, 1)
	go func() {
		_, err := io.Copy(stdin, data)
		stdin.Close()
		copied <- err
	}()
	remote = session.Wait()
	session.Close() // a copy still writing (the command stopped early) fails now
	local = <-copied
	if remote != nil && strings.TrimSpace(errOut.String()) != "" {
		remote = errors.New(strings.TrimSpace(errOut.String()))
	}
	return remote, local
}

// stream runs a command and passes each line it prints (output and errors) to line as it comes
func (c *frameConnection) stream(command string, stdin io.Reader, line func(text string, isError bool)) error {
	session, err := c.client.NewSession()
	if err != nil {
		return err
	}
	defer session.Close()
	stdout, err := session.StdoutPipe()
	if err != nil {
		return err
	}
	stderr, err := session.StderrPipe()
	if err != nil {
		return err
	}
	session.Stdin = stdin
	var lock sync.Mutex
	var readers sync.WaitGroup
	read := func(r io.Reader, isError bool) {
		defer readers.Done()
		scanner := bufio.NewScanner(r)
		scanner.Buffer(make([]byte, 64*1024), 1024*1024)
		for scanner.Scan() {
			lock.Lock()
			line(strings.TrimRight(scanner.Text(), "\r"), isError)
			lock.Unlock()
		}
	}
	readers.Add(2)
	go read(stdout, false)
	go read(stderr, true)
	if err := session.Start(command); err != nil {
		return err
	}
	readers.Wait()
	return session.Wait()
}

// shellQuote quotes a value for the Frame's shell
func shellQuote(s string) string { return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'" }

func formatMB(bytes int64) string { return fmt.Sprintf("%.0f MB", float64(bytes)/1e6) }

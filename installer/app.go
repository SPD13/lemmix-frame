package main

// The installer's state and work: step 1 (find the Frame, check Developer Mode, sign in or pair, check the Frame is ready)
// and step 2 (install, update or uninstall). The page polls GET /api/state and starts the work with POST requests; every API
// request carries the random token of the page's address, so other web pages cannot drive the installer.

import (
	"archive/tar"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

const (
	releaseAPI  = "https://api.github.com/repos/SPD13/lemmix-frame/releases/latest"
	packageName = "lemmix-frame-linux-arm64.tar.gz"
	scriptName  = "frame-install.sh"
	libraryName = "lemmix"
	defaultUser = "steamos"
	defaultHost = "frame.local"
	// Free space on the Frame: the download, the new and the old game side by side while installing; a first install also
	// leaves room for Steam Linux Runtime 4 (about 430 MB) and the NeoLemmix data the game downloads (about 300 MB)
	neededFreeKBUpdate  = 500_000
	neededFreeKBInstall = 1_000_000
	uploadPath          = "$HOME/devkit-game/.lemmix-upload.tar.gz"
)

var loginName = regexp.MustCompile(`^[a-z_][a-z0-9_-]{0,31}$`)

type checkItem struct {
	ID     string   `json:"id"`
	Title  string   `json:"title"`
	Status string   `json:"status"` // idle, running, ok, fail, wait (needs the user: pairing)
	Detail string   `json:"detail"`
	Help   []string `json:"help,omitempty"`
	Action string   `json:"action,omitempty"` // a button the page shows with the help: "trust-key", "pair"
}

type releaseInfo struct {
	Loading   bool   `json:"loading"`
	Error     string `json:"error,omitempty"`
	Version   string `json:"version,omitempty"`
	Date      string `json:"date,omitempty"`
	Size      int64  `json:"size,omitempty"`
	Local     bool   `json:"local,omitempty"` // --archive
	url       string
	digest    string // "sha256:<hex>", from GitHub
	sumURL    string // the package's .sha256
	scriptURL string // the release's frame-install.sh
	path      string
}

type installedInfo struct {
	Known   bool   `json:"known"` // the Frame was checked
	Present bool   `json:"present"`
	Version string `json:"version,omitempty"` // the game's VERSION: "0.1.0 (abc1234)"
	Release string `json:"release,omitempty"` // its first word, what the latest release's version compares with
	Running bool   `json:"running"`
}

type jobLine struct {
	Text string `json:"text"`
	Kind string `json:"kind"` // step, note, error
}

type jobState struct {
	Kind         string    `json:"kind"` // install, uninstall
	Running      bool      `json:"running"`
	Failed       bool      `json:"failed"`
	Message      string    `json:"message,omitempty"`
	Progress     float64   `json:"progress"` // 0..1 while sending, -1 otherwise
	ProgressText string    `json:"progressText,omitempty"`
	Lines        []jobLine `json:"lines"`
}

type appState struct {
	Title       string        `json:"title"`
	Address     string        `json:"address"`
	Frames      []foundFrame  `json:"frames"`
	Discovering bool          `json:"discovering"`
	Checking    bool          `json:"checking"`
	Checks      []checkItem   `json:"checks"`
	Ready       bool          `json:"ready"`
	Installed   installedInfo `json:"installed"`
	Latest      releaseInfo   `json:"latest"`
	Job         *jobState     `json:"job"`
	LibraryName string        `json:"libraryName"`
	KeyComment  string        `json:"keyComment"`
	Version     string        `json:"version"`
	LogPath     string        `json:"logPath"`
	Browser     bool          `json:"browser"`
}

type app struct {
	token string

	mu       sync.Mutex
	state    appState
	conn     *frameConnection
	address  string // as typed, for settings.json
	user     string
	ip       string
	keyName  string
	password string
	busy     bool

	lastPoll atomic.Int64
	quit     chan struct{}
	quitOnce sync.Once
}

func newApp(token, archive string) *app {
	a := &app{token: token, quit: make(chan struct{})}
	a.state = appState{Title: appTitle, LibraryName: libraryName, KeyComment: keyComment(), Version: version, LogPath: logPath,
		Checks: freshChecks(), Latest: releaseInfo{Loading: true}}
	if archive != "" {
		a.state.Latest = localRelease(archive)
	}
	return a
}

func freshChecks() []checkItem {
	return []checkItem{
		{ID: "find", Title: "Frame found on the network", Status: "idle"},
		{ID: "devmode", Title: "Developer Mode is on", Status: "idle"},
		{ID: "login", Title: "Paired with this computer", Status: "idle"},
		{ID: "ready", Title: "Frame ready for Lemmix", Status: "idle"},
	}
}

func (a *app) update(change func(*appState)) {
	a.mu.Lock()
	defer a.mu.Unlock()
	change(&a.state)
}

func (a *app) setCheck(id, status, detail string, help ...string) {
	a.setCheckAction(id, status, detail, "", help...)
}

func (a *app) setCheckAction(id, status, detail, action string, help ...string) {
	logf("check %s: %s: %s", id, status, detail)
	a.update(func(s *appState) {
		for i := range s.Checks {
			if s.Checks[i].ID == id {
				s.Checks[i] = checkItem{ID: id, Title: s.Checks[i].Title, Status: status, Detail: detail, Help: help, Action: action}
			}
		}
	})
}

// start: look up the latest release, find the Frames on the network, and check the likely one (a computer paired before
// signs in by itself)
func (a *app) start() {
	if !a.state.Latest.Local {
		go a.fetchLatest()
	}
	a.update(func(s *appState) { s.Discovering = true })
	frames := discoverFrames(2 * time.Second)
	logf("found on the network: %v", frames)
	last := loadSettings().LastAddress
	address := last
	if address == "" || (len(frames) > 0 && !matchesFrame(last, frames)) {
		address = defaultHost
		if len(frames) > 0 {
			address = frameAddress(frames[0])
		}
	}
	a.update(func(s *appState) {
		s.Discovering = false
		s.Frames = frames
		if s.Address == "" {
			s.Address = address
		}
	})
	a.mu.Lock()
	address = a.state.Address
	a.mu.Unlock()
	a.runChecks(address, "")
}

func frameAddress(f foundFrame) string {
	if f.Host != "" {
		return f.Host
	}
	return f.Address
}

func matchesFrame(address string, frames []foundFrame) bool {
	_, host := splitUser(address)
	for _, f := range frames {
		if strings.EqualFold(host, f.Host) || host == f.Address {
			return true
		}
	}
	return false
}

// splitUser: "user@host" or "host" (user "" then: the Frame's devkit service names it)
func splitUser(address string) (user, host string) {
	address = strings.TrimSpace(address)
	if i := strings.LastIndex(address, "@"); i >= 0 {
		return address[:i], address[i+1:]
	}
	return "", address
}

// runChecks is step 1. A password of "" signs in with the keys only, and asks to pair when they are refused.
func (a *app) runChecks(address, password string) {
	a.mu.Lock()
	if a.busy {
		a.mu.Unlock()
		return
	}
	a.busy = true
	if a.conn != nil {
		a.conn.close()
		a.conn = nil
	}
	a.ip = ""
	a.state.Checking = true
	a.state.Ready = false
	a.state.Address = strings.TrimSpace(address)
	a.state.Checks = freshChecks()
	a.state.Installed = installedInfo{}
	if a.state.Job != nil && !a.state.Job.Running {
		a.state.Job = nil
	}
	frames := a.state.Frames
	a.mu.Unlock()
	defer a.update(func(s *appState) { s.Checking = false })
	defer func() { a.mu.Lock(); a.busy = false; a.mu.Unlock() }()

	user, host := splitUser(address)
	if host == "" {
		a.setCheck("find", "fail", "No address", "Type the Frame's name (frame.local) or its IP address above, then select Connect.")
		return
	}

	// 1. The Frame on the network
	a.setCheck("find", "running", "Looking for "+host+"...")
	ip := ""
	if parsed := net.ParseIP(host); parsed != nil {
		ip = parsed.String()
		a.setCheck("find", "ok", "IP address "+ip)
	} else {
		for _, f := range frames {
			if strings.EqualFold(f.Host, host) || strings.EqualFold(f.Name, host) {
				ip = f.Address
			}
		}
		if ip == "" {
			if found, ok := resolveHost(host, 3*time.Second); ok {
				ip = found
			}
		}
		if ip == "" {
			// The list may have been made before the Frame woke up: look again
			for _, f := range discoverFrames(2 * time.Second) {
				if strings.EqualFold(f.Host, host) || strings.EqualFold(f.Name, host) {
					ip = f.Address
				}
			}
		}
		if ip == "" {
			a.setCheck("find", "fail", host+" is not found on the network",
				"Put the headset on so it is awake: it leaves the network when it sleeps.",
				"The Frame and this computer must be on the same network (the same Wi-Fi or router). A guest network often keeps devices apart, and so can a VPN on this computer.",
				"Developer Mode must be on: on the Frame, **Settings > System > Developer Mode**.",
				"Or type the Frame's IP address above, shown on the Frame in its Wi-Fi settings (the details of the connected network), for example 192.168.1.42. Then select **Connect**.")
			return
		}
		a.setCheck("find", "ok", fmt.Sprintf("%s (%s)", host, ip))
	}

	// 2. Developer Mode: the devkit service answers, and names the Frame's user
	a.setCheck("devmode", "running", "Asking "+ip+"...")
	name, err := devkitGet(ip, "login-name", 8*time.Second)
	if err != nil {
		if strings.Contains(strings.ToLower(err.Error()), "refused") {
			a.setCheck("devmode", "fail", "Developer Mode is off",
				"On the Frame, open **Settings > System** and switch **Developer Mode** on, then select **Check again**.",
				"If "+ip+" is not the Frame's address, type the right one above.")
		} else {
			a.setCheck("devmode", "fail", "No answer from "+ip,
				"Put the headset on so it is awake: it leaves the network when it sleeps.",
				"Check that Developer Mode is on: **Settings > System > Developer Mode**.",
				"Check that "+ip+" is the Frame's address (in the Frame's Wi-Fi settings, the details of the connected network).",
				"Some networks keep devices apart (guest Wi-Fi, \"client isolation\"), and so can a VPN on this computer: use the main network.")
		}
		return
	}
	if user == "" {
		user = strings.TrimSpace(name)
		if !loginName.MatchString(user) {
			user = defaultUser
		}
	}
	a.setCheck("devmode", "ok", "The Frame's devkit service answers")

	a.mu.Lock()
	a.address, a.user, a.ip, a.keyName, a.password = address, user, ip, strings.ToLower(host), password
	a.mu.Unlock()
	// 3, 4
	a.signIn(password, 1)
}

// signIn is check 3 (then 4): ssh to the Frame with the installer's keys, or the password when one is given, trying
// attempts times (sshd may still be starting after a pairing)
func (a *app) signIn(password string, attempts int) {
	a.mu.Lock()
	address, user, ip, keyName := a.address, a.user, a.ip, a.keyName
	a.mu.Unlock()
	a.setCheck("login", "running", "Signing in as "+user+"...")
	var conn *frameConnection
	var err error
	for i := 0; i < attempts; i++ {
		if i > 0 {
			time.Sleep(2 * time.Second)
		}
		if conn, err = dialFrame(user, ip, keyName, password); err == nil {
			break
		}
		logf("sign in: %v", err)
		var changed *errHostKeyChanged
		if errors.As(err, &changed) {
			break
		}
	}
	if err != nil {
		var changed *errHostKeyChanged
		refused := strings.Contains(strings.ToLower(err.Error()), "refused") // sshd is off until the first pairing
		switch {
		case errors.As(err, &changed):
			a.setCheckAction("login", "fail", "The Frame's identity has changed", "trust-key",
				"The Frame does not have the security key it had when this computer last connected to it. This is expected after the Frame was reset or reinstalled.",
				"If you did not reset it, check that the address is really your Frame's. Otherwise, trust its new key.")
		case (refused || strings.Contains(err.Error(), "unable to authenticate")) && password == "":
			a.askToPair("This computer is not paired with the Frame yet")
		case strings.Contains(err.Error(), "unable to authenticate"):
			a.setCheckAction("login", "fail", "The password was refused", "pair",
				"Type the password set on the Frame in **Settings > Developer > Set User Password** (it is case sensitive).",
				"Or pair this computer with the Frame instead: put the headset on, select **Pair with the Frame**, and accept the request in the headset within 30 seconds.")
		case refused:
			a.askToPair("The Frame's ssh server is off: pairing switches it on")
		default:
			a.setCheckAction("login", "fail", "Cannot sign in: "+err.Error(), "pair",
				"Put the headset on so it stays awake, then select **Check again**.")
		}
		return
	}
	a.mu.Lock()
	a.conn = conn
	a.mu.Unlock()
	updateSettings(func(s *settings) { s.LastAddress = address })
	detail := "Signed in as " + user
	if password != "" && a.authorizeInstallerKey(conn) {
		detail += "; paired now, the password is not needed next time"
	}
	a.setCheck("login", "ok", detail)

	// 4. Ready: a Steam Frame, Steam running, room for the game
	a.checkReady(conn)
}

func (a *app) askToPair(detail string) {
	a.setCheckAction("login", "wait", detail, "pair",
		"Put the headset on, with Steam's menus showing (not a game).",
		"Select **Pair with the Frame** below. The headset shows a request from a **development host**: this computer, with the key **"+keyComment()+"**. **Accept it within 30 seconds.**",
		"This is needed once: the next times, the installer signs in by itself.")
}

// pair sends the installer's key to the Frame's devkit service, which asks the wearer to accept it, then signs in with it
func (a *app) pair() {
	a.mu.Lock()
	if a.busy || a.ip == "" {
		a.mu.Unlock()
		return
	}
	a.busy = true
	a.state.Checking = true
	ip := a.ip
	a.mu.Unlock()
	defer a.update(func(s *appState) { s.Checking = false })
	defer func() { a.mu.Lock(); a.busy = false; a.mu.Unlock() }()

	_, public, err := installerKey()
	if err != nil {
		a.setCheckAction("login", "fail", "The installer cannot make its key: "+err.Error(), "pair")
		return
	}
	a.setCheck("login", "running", "Accept the request in the headset within 30 seconds...")
	if err := requestPairing(ip, public); err != nil {
		var refused *errPairingRefused
		if errors.As(err, &refused) {
			a.askToPair("The pairing was declined, or not accepted within 30 seconds")
		} else {
			a.setCheckAction("login", "fail", "The Frame did not answer the pairing request: "+err.Error(), "pair",
				"Put the headset on so it stays awake, then select **Pair with the Frame** again.")
		}
		return
	}
	a.setCheck("login", "running", "Paired. Signing in...")
	a.signIn("", 10)
}

// authorizeInstallerKey adds the installer's key to the Frame's authorized keys, after a sign-in with the password: the
// pairing, without the headset
func (a *app) authorizeInstallerKey(conn *frameConnection) bool {
	_, public, err := installerKey()
	if err != nil {
		logf("installer key: %v", err)
		return false
	}
	q := shellQuote(public)
	_, err = conn.output(`umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; grep -qxF `+q+` ~/.ssh/authorized_keys || printf '%s\n' `+q+` >> ~/.ssh/authorized_keys`, nil)
	if err != nil {
		logf("authorize the installer key: %v", err)
	}
	return err == nil
}

func (a *app) checkReady(conn *frameConnection) {
	a.setCheck("ready", "running", "Checking the Frame...")
	script := `echo "arch=$(uname -m)"
if pgrep -x steam > /dev/null; then echo steam=1; else echo steam=0; fi
echo "free=$(df -Pk "$HOME" | awk 'NR==2 {print $4}')"
if command -v python3 > /dev/null; then echo python=1; else echo python=0; fi
d="$HOME/devkit-game/lemmix"
if [ -x "$d/Lemmix.arm64" ]; then echo installed=1; else echo installed=0; fi
if [ -f "$d/VERSION" ]; then echo "version=$(head -n 1 "$d/VERSION" | head -c 64)"; fi
if pgrep -x Lemmix.arm64 > /dev/null; then echo running=1; else echo running=0; fi
`
	out, err := conn.output("sh -s", strings.NewReader(script))
	if err != nil {
		a.setCheck("ready", "fail", "Cannot check the Frame: "+err.Error(), "Put the headset on so it stays awake, then select **Check again**.")
		return
	}
	values := map[string]string{}
	for _, line := range strings.Split(out, "\n") {
		if key, value, ok := strings.Cut(strings.TrimSpace(line), "="); ok {
			values[key] = value
		}
	}
	free, _ := strconv.ParseInt(values["free"], 10, 64)
	installed := installedInfo{Known: true, Present: values["installed"] == "1", Version: values["version"], Running: values["running"] == "1"}
	installed.Release, _, _ = strings.Cut(installed.Version, " ")
	a.update(func(s *appState) { s.Installed = installed })
	needed := int64(neededFreeKBInstall)
	if installed.Present {
		needed = neededFreeKBUpdate
	}
	switch {
	case values["arch"] != "aarch64":
		a.setCheck("ready", "fail", "This is not a Steam Frame (a "+values["arch"]+" computer)", "Check the address: it must be the Frame's.")
	case values["python"] != "1":
		a.setCheck("ready", "fail", "Python is missing on this device", "SteamOS on the Frame includes it: check that the address is the Frame's.")
	case values["steam"] != "1":
		a.setCheck("ready", "fail", "Steam is not running on the Frame",
			"Put the headset on so it starts in its normal mode (Steam starts with it), then select **Check again**.")
	case free < needed:
		a.setCheck("ready", "fail", fmt.Sprintf("Not enough free space: %s free, %s needed", formatMB(free*1024), formatMB(needed*1024)),
			"Uninstall a game or delete files on the Frame, then select **Check again**.")
	default:
		a.setCheck("ready", "ok", fmt.Sprintf("Steam is running, %s free", formatSpace(free*1024)))
		a.update(func(s *appState) { s.Ready = true })
	}
}

func formatSpace(bytes int64) string {
	if bytes >= 10e9 {
		return fmt.Sprintf("%.0f GB", float64(bytes)/1e9)
	}
	return fmt.Sprintf("%.1f GB", float64(bytes)/1e9)
}

// fetchLatest reads the latest release on GitHub: its version, the package, its checksum and its frame-install.sh
func (a *app) fetchLatest() {
	a.update(func(s *appState) { s.Latest = releaseInfo{Loading: true} })
	info := func() releaseInfo {
		api := releaseAPI
		if override := os.Getenv("LEMMIX_RELEASE_API"); override != "" { // a test release, in GitHub's format
			api = override
		}
		request, _ := http.NewRequest("GET", api, nil)
		request.Header.Set("Accept", "application/vnd.github+json")
		request.Header.Set("User-Agent", "lemmix-frame-installer")
		client := &http.Client{Timeout: 20 * time.Second}
		response, err := client.Do(request)
		if err != nil {
			return releaseInfo{Error: "Cannot reach GitHub to find the latest version. Check this computer's internet connection."}
		}
		defer response.Body.Close()
		if response.StatusCode == 404 {
			return releaseInfo{Error: "No release of Lemmix is published on GitHub yet."}
		}
		if response.StatusCode != 200 {
			return releaseInfo{Error: fmt.Sprintf("GitHub did not give the latest version (%s). Try again in a few minutes.", response.Status)}
		}
		var release struct {
			TagName     string    `json:"tag_name"`
			PublishedAt time.Time `json:"published_at"`
			Assets      []struct {
				Name   string `json:"name"`
				Size   int64  `json:"size"`
				URL    string `json:"browser_download_url"`
				Digest string `json:"digest"`
			} `json:"assets"`
		}
		if err := json.NewDecoder(response.Body).Decode(&release); err != nil {
			return releaseInfo{Error: "GitHub's answer could not be read: " + err.Error()}
		}
		info := releaseInfo{Version: strings.TrimPrefix(release.TagName, "v"), Date: release.PublishedAt.Local().Format("2 January 2006")}
		for _, asset := range release.Assets {
			switch asset.Name {
			case packageName:
				info.Size, info.url, info.digest = asset.Size, asset.URL, asset.Digest
			case packageName + ".sha256":
				info.sumURL = asset.URL
			case scriptName:
				info.scriptURL = asset.URL
			}
		}
		if info.url == "" {
			return releaseInfo{Error: "The latest release on GitHub has no game package (" + packageName + ")."}
		}
		return info
	}()
	logf("latest release: %s %s", info.Version, info.Error)
	a.update(func(s *appState) { s.Latest = info })
}

// localRelease (--archive): the version is the package's own (lemmix/VERSION)
func localRelease(path string) releaseInfo {
	stat, err := os.Stat(path)
	if err != nil {
		return releaseInfo{Error: "Cannot open " + path + ": " + err.Error()}
	}
	version := filepath.Base(path)
	if packaged := packageVersion(path); packaged != "" {
		version, _, _ = strings.Cut(packaged, " ")
	}
	return releaseInfo{Version: version, Date: stat.ModTime().Format("2 January 2006"), Size: stat.Size(), Local: true, path: path}
}

func packageVersion(path string) string {
	file, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer file.Close()
	unzipped, err := gzip.NewReader(file)
	if err != nil {
		return ""
	}
	archive := tar.NewReader(unzipped)
	for {
		header, err := archive.Next()
		if err != nil {
			return ""
		}
		if strings.TrimPrefix(header.Name, "./") == "lemmix/VERSION" {
			data, _ := io.ReadAll(io.LimitReader(archive, 256))
			first, _, _ := strings.Cut(string(data), "\n")
			return strings.TrimSpace(first)
		}
	}
}

// download reads a small file of the release (a checksum, a script)
func download(url string) (string, error) {
	request, _ := http.NewRequest("GET", url, nil)
	request.Header.Set("User-Agent", "lemmix-frame-installer")
	client := &http.Client{Timeout: 30 * time.Second}
	response, err := client.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return "", errors.New(response.Status)
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, 1024*1024))
	return string(data), err
}

// connection gives the open connection, signing in again (same keys or password) when the Frame dropped it, for instance
// after a sleep
func (a *app) connection() (*frameConnection, error) {
	a.mu.Lock()
	conn, user, ip, keyName, password := a.conn, a.user, a.ip, a.keyName, a.password
	a.mu.Unlock()
	if conn != nil && conn.alive() {
		return conn, nil
	}
	if ip == "" {
		return nil, errors.New("not connected")
	}
	conn, err := dialFrame(user, ip, keyName, password)
	if err != nil {
		return nil, err
	}
	a.mu.Lock()
	a.conn = conn
	a.mu.Unlock()
	return conn, nil
}

// beginJob marks the installer busy with a job, or says why it cannot start
func (a *app) beginJob(kind string) (*jobState, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.busy {
		return nil, errors.New("busy")
	}
	if !a.state.Ready {
		return nil, errors.New("the Frame is not ready: finish step 1")
	}
	a.busy = true
	job := &jobState{Kind: kind, Running: true, Progress: -1}
	a.state.Job = job
	return job, nil
}

func (a *app) endJob(job *jobState, failure string) {
	a.update(func(s *appState) {
		job.Running = false
		job.Progress = -1
		job.ProgressText = ""
		logf("%s finished: %s", job.Kind, failure)
		if failure != "" {
			job.Failed = true
			job.Message = failure
		}
	})
	a.mu.Lock()
	a.busy = false
	a.mu.Unlock()
	// What is on the Frame now
	if conn, err := a.connection(); err == nil {
		a.checkReady(conn)
	}
}

func (a *app) addLine(job *jobState, text string, isError bool) {
	if strings.TrimSpace(text) == "" {
		return
	}
	// frame-install.sh stops with "error: <why>" on its error output; curl and tar report there too, so that alone does not
	// make a line an error
	line := jobLine{Text: strings.TrimSpace(text), Kind: "note"}
	switch {
	case strings.HasPrefix(text, "==> "):
		line = jobLine{Text: strings.TrimPrefix(text, "==> "), Kind: "step"}
	case strings.HasPrefix(text, "error: "):
		line = jobLine{Text: strings.TrimPrefix(line.Text, "error: "), Kind: "error"}
	}
	logf("%s %s: %s", job.Kind, line.Kind, line.Text)
	a.update(func(s *appState) { job.Lines = append(job.Lines, line) })
}

func lostConnectionMessage(err error) string {
	return "The connection to the Frame was lost (" + err.Error() + "). Put the headset on so it stays awake, then install again: it is always safe."
}

// installScript: the release's own frame-install.sh, so that each release installs itself the way it was made for, else
// the copy built into the installer
func (a *app) installScript(latest releaseInfo) string {
	if latest.scriptURL != "" {
		script, err := download(latest.scriptURL)
		if err == nil && strings.HasPrefix(script, "#!") {
			return strings.ReplaceAll(script, "\r", "")
		}
		logf("the release's %s: %v; using the installer's own", scriptName, err)
	}
	return frameInstallScript
}

// expectedSum: the package's sha256, from GitHub's digest or the .sha256 beside it ("" when neither is there)
func expectedSum(latest releaseInfo) string {
	if want, ok := strings.CutPrefix(latest.digest, "sha256:"); ok {
		return strings.ToLower(want)
	}
	text := ""
	if latest.Local {
		data, _ := os.ReadFile(latest.path + ".sha256")
		text = string(data)
	} else if latest.sumURL != "" {
		text, _ = download(latest.sumURL)
	}
	if fields := strings.Fields(text); len(fields) > 0 && len(fields[0]) == 64 {
		return strings.ToLower(fields[0])
	}
	return ""
}

// install is step 2: the package, from GitHub or a local file, is sent through ssh straight to the Frame (no copy on this
// computer) and checked, then the release's frame-install.sh installs it and registers it with Steam
func (a *app) install() {
	job, err := a.beginJob("install")
	if err != nil {
		return
	}
	a.mu.Lock()
	latest := a.state.Latest
	a.mu.Unlock()
	if latest.Error != "" || (latest.url == "" && latest.path == "") {
		a.endJob(job, "Lemmix cannot be downloaded: "+latest.Error)
		return
	}
	conn, err := a.connection()
	if err != nil {
		a.endJob(job, lostConnectionMessage(err))
		return
	}
	script := a.installScript(latest)
	want := expectedSum(latest)

	// The package
	a.addLine(job, "==> Downloading and sending Lemmix "+latest.Version, false)
	var source io.ReadCloser
	total := latest.Size
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if latest.Local {
		file, err := os.Open(latest.path)
		if err != nil {
			a.endJob(job, "Cannot open "+latest.path+": "+err.Error())
			return
		}
		source = file
	} else {
		request, _ := http.NewRequestWithContext(ctx, "GET", latest.url, nil)
		request.Header.Set("User-Agent", "lemmix-frame-installer")
		response, err := http.DefaultClient.Do(request)
		if err != nil {
			a.endJob(job, "The download from GitHub did not start: "+err.Error()+". Check this computer's internet connection, then install again.")
			return
		}
		if response.StatusCode != 200 {
			response.Body.Close()
			a.endJob(job, "The download from GitHub failed ("+response.Status+"). Try again in a few minutes.")
			return
		}
		if response.ContentLength > 0 {
			total = response.ContentLength
		}
		source = response.Body
	}
	defer source.Close()

	hash := sha256.New()
	reader := &progressReader{source: source}
	reader.lastRead.Store(time.Now().UnixNano())
	// The page's progress bar; no progress for a minute means GitHub or the Frame stalled: closing the download then ends the
	// transfer with an error
	var stalled atomic.Bool
	stop := make(chan struct{})
	go func() {
		ticker := time.NewTicker(300 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-stop:
				return
			case <-ticker.C:
			}
			if time.Since(time.Unix(0, reader.lastRead.Load())) > time.Minute {
				stalled.Store(true)
				cancel()
				source.Close()
				return
			}
			done := reader.count.Load()
			a.update(func(s *appState) {
				if total > 0 {
					job.Progress = float64(done) / float64(total)
				}
				job.ProgressText = fmt.Sprintf("%s of %s", formatMB(done), formatMB(total))
			})
		}
	}()
	removeUpload := func() { conn.output(`rm -f "`+uploadPath+`"`, nil) }
	sendErr, readErr := conn.send(`set -e; mkdir -p "$HOME/devkit-game"; cat > "`+uploadPath+`"`, io.TeeReader(reader, hash))
	close(stop)
	switch {
	case readErr != nil:
		message := "The download stopped: " + readErr.Error() + "."
		if stalled.Load() {
			message = "The download stopped: nothing arrived for a minute."
		}
		removeUpload()
		a.endJob(job, message+" Check this computer's internet connection and that the headset is awake, then install again.")
		return
	case sendErr != nil:
		if !conn.alive() {
			a.endJob(job, lostConnectionMessage(sendErr))
		} else {
			removeUpload()
			a.endJob(job, "Lemmix could not be sent to the Frame: "+sendErr.Error())
		}
		return
	}
	a.update(func(s *appState) { job.Progress = -1; job.ProgressText = "" })
	a.addLine(job, "    "+formatMB(reader.count.Load())+" sent", false)
	if got := hex.EncodeToString(hash.Sum(nil)); want != "" && want != got {
		removeUpload()
		a.endJob(job, "The download was damaged (its checksum does not match). Install again.")
		return
	} else if want != "" {
		a.addLine(job, "    checksum ok", false)
	}

	// The release's installer, on the Frame
	a.addLine(job, "==> Installing on the Frame", false)
	command := `bash -s -- --file "` + uploadPath + `"; status=$?; rm -f "` + uploadPath + `"; exit $status`
	// frame-install.sh ends with "Done." and what to do next, which the page's closing banner says already
	closing := false
	err = conn.stream(command, strings.NewReader(script), func(text string, isError bool) {
		closing = closing || (!isError && strings.HasPrefix(text, "Done."))
		if !closing || isError {
			a.addLine(job, text, isError)
		}
	})
	if err != nil {
		if !conn.alive() {
			a.endJob(job, lostConnectionMessage(err))
		} else {
			a.endJob(job, "The installation on the Frame did not finish (see the messages above). Installing again is always safe.")
		}
		return
	}
	a.endJob(job, "")
}

// uninstall removes Lemmix and its library entry, and with purge the game data it downloaded
func (a *app) uninstall(purge bool) {
	job, err := a.beginJob("uninstall")
	if err != nil {
		return
	}
	conn, err := a.connection()
	if err != nil {
		a.endJob(job, lostConnectionMessage(err))
		return
	}
	a.addLine(job, "==> Removing Lemmix from the Frame", false)
	command := "bash -s -- --uninstall"
	if purge {
		command += " --purge"
	}
	err = conn.stream(command, strings.NewReader(frameInstallScript), func(text string, isError bool) { a.addLine(job, text, isError) })
	if err != nil {
		a.endJob(job, "Lemmix could not be removed (see the messages above).")
		return
	}
	a.endJob(job, "")
}

// trustNewKey forgets the Frame's old host key, then signs in again
func (a *app) trustNewKey(password string) {
	a.mu.Lock()
	address := a.state.Address
	a.mu.Unlock()
	_, host := splitUser(address)
	updateSettings(func(s *settings) { delete(s.HostKeys, strings.ToLower(host)) })
	a.runChecks(address, password)
}

type progressReader struct {
	source   io.Reader
	count    atomic.Int64
	lastRead atomic.Int64
}

func (r *progressReader) Read(p []byte) (int, error) {
	n, err := r.source.Read(p)
	if n > 0 {
		r.count.Add(int64(n))
		r.lastRead.Store(time.Now().UnixNano())
	}
	return n, err
}

// The page's API

type requestBody struct {
	Address  string `json:"address"`
	Password string `json:"password"`
	Purge    bool   `json:"purge"`
}

func (a *app) routes(mux *http.ServeMux) {
	handle := func(path string, method string, f func(w http.ResponseWriter, r *http.Request)) {
		mux.HandleFunc(path, func(w http.ResponseWriter, r *http.Request) {
			if r.Method != method || r.Header.Get("X-Token") != a.token {
				http.Error(w, "forbidden", http.StatusForbidden)
				return
			}
			f(w, r)
		})
	}
	decode := func(r *http.Request) (requestBody, error) {
		var b requestBody
		err := json.NewDecoder(io.LimitReader(r.Body, 64*1024)).Decode(&b)
		return b, err
	}
	handle("/api/state", "GET", func(w http.ResponseWriter, r *http.Request) {
		a.lastPoll.Store(time.Now().UnixNano())
		a.mu.Lock()
		data, _ := json.Marshal(a.state)
		a.mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")
		w.Write(data)
	})
	handle("/api/connect", "POST", func(w http.ResponseWriter, r *http.Request) {
		b, err := decode(r)
		if err != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		go a.runChecks(b.Address, b.Password)
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/pair", "POST", func(w http.ResponseWriter, r *http.Request) {
		go a.pair()
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/trust-key", "POST", func(w http.ResponseWriter, r *http.Request) {
		b, _ := decode(r)
		go a.trustNewKey(b.Password)
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/discover", "POST", func(w http.ResponseWriter, r *http.Request) {
		go func() {
			a.update(func(s *appState) { s.Discovering = true })
			frames := discoverFrames(2 * time.Second)
			a.update(func(s *appState) { s.Discovering = false; s.Frames = frames })
		}()
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/latest", "POST", func(w http.ResponseWriter, r *http.Request) {
		a.mu.Lock()
		local := a.state.Latest.Local
		a.mu.Unlock()
		if !local {
			go a.fetchLatest()
		}
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/install", "POST", func(w http.ResponseWriter, r *http.Request) {
		go a.install()
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/uninstall", "POST", func(w http.ResponseWriter, r *http.Request) {
		b, _ := decode(r)
		go a.uninstall(b.Purge)
		w.WriteHeader(http.StatusNoContent)
	})
	// Only the page's own links: a page must not be able to open anything else
	links := map[string]bool{"https://github.com/SPD13/lemmix-frame": true, "http://spd13.us/": true, "http://spd13.us": true}
	handle("/api/open", "POST", func(w http.ResponseWriter, r *http.Request) {
		var b struct {
			URL string `json:"url"`
		}
		if json.NewDecoder(io.LimitReader(r.Body, 4096)).Decode(&b) != nil || !links[b.URL] {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		openBrowser(b.URL)
		w.WriteHeader(http.StatusNoContent)
	})
	handle("/api/quit", "POST", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNoContent)
		a.quitOnce.Do(func() { close(a.quit) })
	})
}

// waitForPageClosed (browser only): returns when Quit is pressed, or when the page stopped asking for the state (closed tab)
// while no job runs
func (a *app) waitForPageClosed(ctx context.Context) {
	a.update(func(s *appState) { s.Browser = true })
	opened := time.Now()
	for {
		select {
		case <-a.quit:
			return
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
		a.mu.Lock()
		busy := a.busy
		a.mu.Unlock()
		last := a.lastPoll.Load()
		if busy {
			continue
		}
		if last == 0 && time.Since(opened) > 30*time.Minute {
			return
		}
		if last != 0 && time.Since(time.Unix(0, last)) > 20*time.Second {
			return
		}
	}
}

func (a *app) shutdown() {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.conn != nil {
		a.conn.close()
	}
}

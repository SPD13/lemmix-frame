// Lemmix for Steam Frame installer: a desktop app (Windows, macOS, Linux) that finds a Steam Frame on the network, pairs
// with it (the headset asks the wearer to accept this computer, as the SteamOS Devkit Client does), checks it can be
// installed to (Developer Mode, Steam running, free space), then downloads the latest release from GitHub and installs or
// updates Lemmix on the Frame, all over its own ssh connection: nothing else needs to be installed on the computer.
//
// The interface is a web page (ui/) served on 127.0.0.1 and shown in a native window (WebView2 on Windows, WebKit on macOS);
// on Linux, or when no web view is available, it opens in the default browser and the app quits when the page is closed.
// The steps on the Frame are those of the release itself: its frame-install.sh (the copy embedded at build time, see
// tools/make-installer.sh, when the release has none or for --archive).
//
//	lemmix-frame-installer [--browser [--no-open]] [--archive FILE.tar.gz]
//
//	--browser         show the interface in the default browser instead of a window
//	--no-open         with --browser: only print the address to open
//	--archive FILE    install this package instead of downloading the latest release (testing, offline installs)
//
// LEMMIX_RELEASE_API overrides where the latest release is read from (a JSON in the format of GitHub's releases/latest), to
// try a release before publishing it.
package main

import (
	"context"
	"crypto/rand"
	"embed"
	"encoding/hex"
	"flag"
	"fmt"
	"io/fs"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"time"
)

const appTitle = "Lemmix for Steam Frame - Installer"

var version = "dev" // set by tools/make-installer.sh

//go:embed ui
var uiFiles embed.FS

//go:embed embedded/frame-install.sh
var frameInstallScript string

func main() {
	browser := flag.Bool("browser", false, "show the interface in the default browser")
	noOpen := flag.Bool("no-open", false, "with --browser: only print the address to open")
	archive := flag.String("archive", "", "install this package (.tar.gz) instead of the latest release")
	flag.Parse()

	openLog()
	logf("%s %s on %s/%s", appTitle, version, runtime.GOOS, runtime.GOARCH)

	tokenBytes := make([]byte, 16)
	rand.Read(tokenBytes)
	app := newApp(hex.EncodeToString(tokenBytes), *archive)

	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fatal("cannot start: " + err.Error())
	}
	ui, _ := fs.Sub(uiFiles, "ui")
	mux := http.NewServeMux()
	mux.Handle("/", http.FileServer(http.FS(ui)))
	app.routes(mux)
	go http.Serve(listener, mux)
	url := fmt.Sprintf("http://%s/#%s", listener.Addr(), app.token)

	go app.start()

	if !*browser && openWindow(url) {
		app.shutdown()
		return
	}
	// Browser: the app runs until the page is closed (it polls the state while open) or the Quit button is pressed
	fmt.Println(appTitle)
	fmt.Println("The installer is open in your browser. If it did not open, go to:")
	fmt.Println("   " + url)
	if !*noOpen {
		openBrowser(url)
	}
	app.waitForPageClosed(context.Background())
	app.shutdown()
}

func openBrowser(url string) {
	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "windows":
		cmd = exec.Command("rundll32", "url.dll,FileProtocolHandler", url)
	case "darwin":
		cmd = exec.Command("open", url)
	default:
		cmd = exec.Command("xdg-open", url)
	}
	if err := cmd.Start(); err == nil {
		go cmd.Wait()
	}
}

// installer.log, in the user's cache folder (shown on the page): what the checks found and what the install printed, for help
// when something goes wrong
var logFile *os.File
var logPath string

func openLog() {
	dir, err := os.UserCacheDir()
	if err != nil {
		return
	}
	dir = filepath.Join(dir, "lemmix-frame-installer")
	os.MkdirAll(dir, 0o700)
	logPath = filepath.Join(dir, "installer.log")
	logFile, _ = os.Create(logPath)
}

func logf(format string, args ...any) {
	if logFile != nil {
		fmt.Fprintf(logFile, time.Now().Format("15:04:05 ")+format+"\n", args...)
	}
}

func fatal(message string) {
	fmt.Fprintln(os.Stderr, message)
	showError(message)
	os.Exit(1)
}

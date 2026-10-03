# The regression gate: `make verify` must be green before a parity row is marked done.
SHELL := /bin/bash
WEB_ASSETS ?= $(abspath ../LemmingsJS)
export WEB_ASSETS

.PHONY: verify core core-linux app-smoke app-test linux-smoke export-linux probe-virtual matrix libopenmpt

verify: core core-linux app-smoke app-test linux-smoke

core:                      ## Lemmix.Core tests on the Mac
	cd core/Lemmix.Core.Tests && dotnet run -c Release

core-linux:                ## the same tests in the Frame's runtime (sniper arm64), case-sensitive filesystem
	tools/test-linux.sh

app-smoke:                 ## the Godot app starts headless on the Mac
	cd app && dotnet build -v q
	tools/godot-run.sh 90 "smoke ok" --headless --xr-mode off --path app -- --smoke >/dev/null

app-test:                  ## the app's own tests (scripted VR input, scenes), headless on the Mac
	cd app && dotnet build -v q
	tools/godot-run.sh 180 "tests done" --headless --xr-mode off --path app -- --test | grep -E "^\[test\] FAIL|tests done" ; \
	tools/godot-run.sh 180 "tests done" --headless --xr-mode off --path app -- --test | grep -q "tests done: [0-9]* run, 0 failed"

linux-smoke: export-linux  ## the exported build starts in the Frame's runtime (sniper arm64)
	tools/run-linux.sh build/linux-smoke --smoke | grep -q "smoke ok"

export-linux:              ## build/app/linux-arm64: what goes to the Frame
	tools/export.sh linux-arm64

probe-virtual:             ## phase 0 off-device probe in sniper arm64
	tools/probe-virtual.sh

matrix:                    ## regenerate parity/matrix.json from the web sources
	node parity/gen-matrix.js

libopenmpt:
	native/libopenmpt/build.sh all

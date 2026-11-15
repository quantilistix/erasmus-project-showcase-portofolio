# Build and run: wave_sim

This browser program needs no compilation. Install a modern JavaScript-enabled
browser (Firefox, Chrome, Edge or Safari) for your OS. Desktop pointer/keyboard
behavior is the intended interface; mobile usability is unverified.

## Run

Open `wave_sim.html` in your browser. For consistent browser permissions and local file
loading, use a localhost server with [Python 3](../../BUILDNRUN.md#start-here).
From this project directory:

```sh
python3 ../../tools/buildnrun.py run vibe-coded/wave_sim --port 8080
```

On Windows use `py -3` instead of `python3`. Open
`http://127.0.0.1:8080/wave_sim.html` and stop the server with Ctrl+C.
If 8080 is occupied, use `--port 8081`. The server listens only on this computer.
There is no automatic browser launch.

Alternative without the common launcher, from this directory:

```sh
python3 -m http.server 8080 --bind 127.0.0.1
```

Keep all supplied HTML, CSS and JavaScript files together. No npm installation is
needed for normal use. If a browser blocks something under `file://`, use the
localhost URL and check its developer console. A server does not add unsupported
browser APIs; use an up-to-date browser if a feature is missing.

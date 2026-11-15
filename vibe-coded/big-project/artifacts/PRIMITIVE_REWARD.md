# Historical catch reward update

This report predates the maze game. The Catch readout was retrained for 6,000 frames to predict quadratic proximity only: `(clamp(1-distance/19,0,1))²`. Catching gave a separate primitive +1 pulse with a decaying sensory trace. Learned reward weight, pain firing strength and pleasure gain were reduced to one quarter of their former values.

`primitive-reward-main-preservation.json` verifies existing main parameters and plasticity survived adding 16 zero-weight primitive input ports. Main update count stayed 10,704; reward updates increased from 32,000 to 38,000. The head was frozen afterward. `primitive-reward-evaluation.json` records held-out pleasure MSE 0.109650 versus 0.127194 before recalibration; this does not establish policy improvement or immunity to reward hacking.

The current app has moved to the maze experiment described in `MAZE_RESULTS.md`; these saved arcade models are retained separately for historical comparison.

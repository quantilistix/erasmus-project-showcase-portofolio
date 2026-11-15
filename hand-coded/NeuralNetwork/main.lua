--Random function (use ```Random.seed(seed)``` to set the seed and ```Random.rnd(rnd)``` to randomize the seed and give you a random number).
Random = {}

--Set the seed of the function.
Random.seed = 0

--Randomizer.
function Random.rnd()
    Random.seed = (1103515245 * Random.seed + 12345) % 2 ^ 31
    return Random.seed / 2 ^ 31
end


Talvo = {}

Talvo.new = require("neuralNetwork")


--Make a fast fourier transform
local FFT = require("FFT")

--Talvo.new(8, { 3220, 256, 32, 32, 32, 32, 64, 128 })
--120 neurons & 2531 (2.5k) weights.
Talvo:new(8, { 5 ^ 2, 16, 16, 15, 14, 13, 11, 10 })


--Reset it's brain
Talvo.reset(3)


--Do learning
Talvo.var.correction = {}
for n = 1, Talvo.var.numNeurons[Talvo.var.numLayers] do
    Talvo.var.correction[n] = 0
end
Talvo.var.correction[8] = 1

--Time for torture!
function Talvo.cost(res)
    local cost = 0
    for n = 1, Talvo.var.numNeurons[Talvo.var.numLayers] do
        cost = cost + (res[n] - Talvo.var.correction[n]) ^ 2
    end
    return cost
end

--Set the neuralNetwork's inputs
Inp = {}
for i = 1, Talvo.var.numNeurons[1] do
    Inp[i] = Random.rnd()
end

Talvo.think(Inp)

for i = 0, 99 do
    Talvo.learn(Inp, 7)
end
Talvo.save("NN.dat")

--Fourier Transform of the inputs
--FFTresults = FFT(Inp)

function love.draw()
    Talvo.draw(60, 20)
    local l = math.sqrt(Talvo.var.numNeurons[1])

    for i = 0, Talvo.var.numNeurons[1] - 1 do
        love.graphics.setColor(Talvo.var.neurons[1][i + 1], Talvo.var.neurons[1][i + 1], Talvo.var.neurons[1][i + 1], 1)
        love.graphics.rectangle("fill", (i - math.floor(i / l) * l) * 10 + 500, math.floor(i / l) * 10, 10, 10)
    end

    -- love.graphics.setColor(1, 1, 1, 1)
    -- for i = 1, Talvo.var.numNeurons[1] do
    --     love.graphics.points(i + 400, FFTresults.amplitude[i] * 20 + 400)
    --     --love.graphics.points(i + 400, FFTresults.dephase[i] * 20 + 400)
    -- end
end

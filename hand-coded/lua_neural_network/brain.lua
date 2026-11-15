

function tanh(x)
    if x > 10 then return 1 end
    if x < -10 then return -1 end
    return (math.exp(2*x) - 1) / (math.exp(2*x) + 1)
end

function brain_init()

    brain_neuronCount = 5 + 32 + 32 + 2

    brain_neurons = {}
    brain_neurons[0] = {}
    brain_neurons[1] = {}

    brain_weightCount = 5*32 + 32*32 + 32*2
    --dim float brain_weights(brain_weightCount-1, 2)
    brain_weights = {}


    for neuron = 0, brain_neuronCount - 1 do
        brain_neurons[0][neuron] = {}
        brain_neurons[0][neuron][0] = 0
        brain_neurons[0][neuron][1] = 0
        brain_neurons[0][neuron][2] = love.math.random() * 256 + 400 - 128
        brain_neurons[0][neuron][3] = love.math.random() * 256 + 128 - 128
        brain_neurons[1][neuron] = {}
        brain_neurons[1][neuron][0] = brain_neurons[0][neuron][0]
        brain_neurons[1][neuron][1] = brain_neurons[0][neuron][1]
        brain_neurons[1][neuron][2] = brain_neurons[0][neuron][2]
        brain_neurons[1][neuron][3] = brain_neurons[0][neuron][3]
    end

    weight = 0

    for i=0, 4 do
        for j=0, 31 do
            brain_weights[weight] = {}
            brain_weights[weight][0] = love.math.random() * 8 - 4
            brain_weights[weight][1] = i
            brain_weights[weight][2] = j + 5
            weight = weight + 1
        end
    end

    for i=0, 31 do
        for j=0, 31 do
            brain_weights[weight] = {}
            brain_weights[weight][0] = love.math.random() * 8 - 4
            brain_weights[weight][1] = i + 5
            brain_weights[weight][2] = j + 5 + 32
            weight = weight + 1
        end
    end

    for i=0, 31 do
        for j=0, 1 do
            brain_weights[weight] = {}
            brain_weights[weight][0] = love.math.random() * 8 - 4
            brain_weights[weight][1] = i + 5 + 32
            brain_weights[weight][2] = j + 5 + 32 + 32
            weight = weight + 1
        end
    end

    local dx, dy, dist, force

    love.graphics.print("Calibrating neurons...")

    for t = 0, 255 do

        love.graphics.rectangle("fill", math.floor(10+t*780/256), 20, math.floor(10+(t+1)*780/256)-math.floor(10+t*780/256)-1, 10)
        for i = 0, brain_neuronCount - 2 do
            for j = i + 1, brain_neuronCount - 1 do
                dx = brain_neurons[0][i][2] - brain_neurons[0][j][2]
                dy = brain_neurons[0][i][3] - brain_neurons[0][j][3]
                dist = math.sqrt(dx*dx+dy*dy)
                force = (dist - 128) * 0.01 / dist
                brain_neurons[1][i][2] = brain_neurons[0][i][2] - force * dx
                brain_neurons[1][i][3] = brain_neurons[0][i][3] - force * dy
                brain_neurons[1][j][2] = brain_neurons[0][j][2] + force * dx
                brain_neurons[1][j][3] = brain_neurons[0][j][3] + force * dy
                brain_neurons[0][i][2] = brain_neurons[1][i][2]
                brain_neurons[0][i][3] = brain_neurons[1][i][3]
                brain_neurons[0][j][2] = brain_neurons[1][j][2]
                brain_neurons[0][j][3] = brain_neurons[1][j][3]
            end
        end
        brain_draw()
        love.graphics.present()

    end
end

function brain_think(dt)

    for weight = 0, brain_weightCount - 1 do
        brain_neurons[1][brain_weights[weight][1]][0] = brain_neurons[1][brain_weights[weight][1]][0] + brain_neurons[0][brain_weights[weight][2]][1] * brain_weights[weight][0] * dt
        brain_neurons[1][brain_weights[weight][2]][1] = brain_neurons[1][brain_weights[weight][2]][1] - brain_neurons[0][brain_weights[weight][1]][0] * brain_weights[weight][0] * dt
    end
    for neuron = 0, brain_neuronCount - 1 do
        brain_neurons[0][neuron][0] = tanh(brain_neurons[1][neuron][0] - brain_neurons[0][neuron][1] * dt)
        brain_neurons[0][neuron][1] = tanh(brain_neurons[1][neuron][1] + brain_neurons[0][neuron][0] * dt)
        brain_neurons[1][neuron][0] = brain_neurons[0][neuron][0]
        brain_neurons[1][neuron][1] = brain_neurons[0][neuron][1]
    end

end


function brain_error()
    local err = 0
    for neuron = 0, brain_neuronCount - 1 do
        err = err + brain_neurons[0][neuron][1]^2
    end
    return err
end

function brain_learn(dt)
    for weight = 0, brain_weightCount - 1 do
        brain_weights[weight][0] = brain_weights[weight][0]+(brain_neurons[0][brain_weights[weight][1]][1] * brain_neurons[0][brain_weights[weight][2]][0])*dt
    end
end

function brain_draw()

    local a, e, w
    for weight = 0, brain_weightCount - 1 do
        w = math.max(math.min((brain_weights[weight][0] + 1) * 0.5, 1), 0)
        love.graphics.setColor(1 - w, 1 - math.abs(w - 0.5) * 2, w, 1)
        love.graphics.line(
            brain_neurons[0][brain_weights[weight][1]][2],
            brain_neurons[0][brain_weights[weight][1]][3],
            brain_neurons[0][brain_weights[weight][2]][2],
            brain_neurons[0][brain_weights[weight][2]][3]
        )
    end
    for neuron = 0, brain_neuronCount - 1 do
        a = math.min(math.max((brain_neurons[0][neuron][0]+1)*0.5, 0), 1)
        e = math.min(math.max((brain_neurons[0][neuron][1]+1)*0.5, 0), 1)
        love.graphics.setColor(e, a-e, a, 1)
        love.graphics.circle("fill", brain_neurons[0][neuron][2], brain_neurons[0][neuron][3], 4)
        love.graphics.setColor(1, 1, 1, 1)
        love.graphics.circle("line", brain_neurons[0][neuron][2], brain_neurons[0][neuron][3], 4)
    end

end


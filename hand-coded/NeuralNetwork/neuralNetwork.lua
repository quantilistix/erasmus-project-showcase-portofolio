--This is colection NeuralNetwork ```aka``` NN. It returns functions such as: \
--```NN.new``` - create new; \
--```NN.reset``` - reset weights and biases; \
--```NN.think``` - returns a table with all outputs (answers); \
--```NN.draw``` - draws the neuralNetwork on the screen; \
--```NN.var``` - contains all variables (not a function). \
-- \
--__Note__ that this colection __uses the graphics library LOVE2D__!

--Create new. \
--Layers must be the number of layers of the NeuralNetwork ```aka``` NN. \
--Neurons a table value large as Layers to contain how many neurons for each layer. \
--```layers``` - number; \
--```neurons``` - table[layers] number.
local function new(self, layers, neurons)
    --All variables in a colection are here.
    self.var = {}

    --The number of layers.
    self.var.numLayers = layers

    --The number of neurons.
    self.var.numNeurons = neurons

    --The neurons activations.
    self.var.neurons = {}

    --The weights for each coselfection.
    self.var.weights = {}

    --The bias for each neuron.
    self.var.bias = {}

    --Seting the tables
    for l = 1, self.var.numLayers do
        self.var.neurons[l] = {}
        for n = 1, self.var.numNeurons[l] do
            self.var.neurons[l][n] = 0
        end
    end
    for l = 1, self.var.numLayers - 1 do
        self.var.weights[l] = {}
        self.var.bias[l] = {}
        for n1 = 1, self.var.numNeurons[l] do
            self.var.weights[l][n1] = {}
            self.var.bias[l][n1] = 0
            for n2 = 1, self.var.numNeurons[l + 1] do
                self.var.weights[l][n1][n2] = 0
            end
        end
    end

    --Reset the NeuralNet. \
    --Enter seed. \
    --```seed``` - number
    function self.reset(seed)
        Random.seed = seed
        for l = 1, self.var.numLayers do
            for n = 1, self.var.numNeurons[l] do
                self.var.neurons[l][n] = 0
            end
        end
        for l = 1, self.var.numLayers - 1 do
            for n1 = 1, self.var.numNeurons[l] do
                self.var.bias[l][n1] = Random.rnd() * 2 - 1
                for n2 = 1, self.var.numNeurons[l + 1] do
                    self.var.weights[l][n1][n2] = Random.rnd() * 2 - 1
                end
            end
        end
    end

    --Think! \
    --Input must be a table full of numbers. How many? Use (__function__)```.var.numNeurons[1]```. \
    --```returns``` - a table. \
    --The biases don't work yet!!!
    function self.think(inp)
        local out = 0
        self.var.neurons[1] = inp

        for l = 1, self.var.numLayers - 1 do
            for n1 = 1, self.var.numNeurons[l + 1] do
                for n2 = 1, self.var.numNeurons[l] do
                    out = out + self.var.neurons[l][n2] * self.var.weights[l][n2][n1]
                end
                --out = 1 / (1 + math.exp(-(out + self.var.bias[l][n1])))
                out = 1 / (1 + math.exp(-out))
                self.var.neurons[l + 1][n1] = out
            end
        end

        return self.var.neurons[self.var.numLayers]
    end

    --This function is use along with the (__function__)```.cost``` function. \
    --The cost function must be constructed by you { (__function__)```.cost``` }. \
    --The cost function must return a number. This number should reprezent how bad is the neuralNetwork doing. \
    --The ```inp``` is just like the (__function__).think. \
    --The ```accuracy``` is the accuracy of the function (10 recommended).
    function self.learn(inp, accuracy)
        local dCost
        local cost1
        local cost2
        cost1 = self.cost(self.think(inp))
        for l = 1, self.var.numLayers - 1 do
            for n1 = 1, self.var.numNeurons[l + 1] do
                for n2 = 1, self.var.numNeurons[l] do
                    self.var.weights[l][n2][n1] = self.var.weights[l][n2][n1] + accuracy
                    cost2 = self.cost(self.think(inp))
                    dCost = cost1 - cost2
                    self.var.weights[l][n2][n1] = self.var.weights[l][n2][n1] - accuracy
                    self.var.weights[l][n2][n1] = self.var.weights[l][n2][n1] + dCost / accuracy
                end
            end
        end
    end

    --This function draws the neuralNetwork using the graphics library LOVE2D.
    function self.draw(xs, ys)
        local largestRecord = -1
        for l = 1, self.var.numLayers do
            if self.var.numNeurons[l] > largestRecord or largestRecord == -1 then
                largestRecord = self.var.numNeurons[l]
            end
        end
        for l = 1, self.var.numLayers - 1 do
            for n1 = 1, self.var.numNeurons[l] do
                for n2 = 1, self.var.numNeurons[l + 1] do
                    if self.var.weights[l][n1][n2] > 0 then
                        love.graphics.setColor(0, self.var.weights[l][n1][n2], 0, 0.5)
                    else
                        love.graphics.setColor(math.abs(self.var.weights[l][n1][n2]), 0, 0, 0.5)
                    end
                    love.graphics.line(l * xs + 10, n1 * ys + (largestRecord - self.var.numNeurons[l]) * ys / 2,
                        (l + 1) * xs + 10,
                        n2 * ys + (largestRecord - self.var.numNeurons[l + 1]) * ys / 2)
                end
            end
        end
        for l = 1, self.var.numLayers do
            for n1 = 1, self.var.numNeurons[l] do
                love.graphics.setColor(self.var.neurons[l][n1], self.var.neurons[l][n1], self.var.neurons[l][n1], 1)
                love.graphics.circle("fill", l * xs + 10, n1 * ys + (largestRecord - self.var.numNeurons[l]) * ys / 2,
                    ys /
                    3)
            end
        end
        love.graphics.setColor(1, 1, 1, 1)
        if self.cost then
            love.graphics.print(self.cost(self.var.neurons[self.var.numLayers]))
        elseif self.cost == nil then
            love.graphics.print("nil")
        end
    end

    --This function saves the neuralNetwork in a .dat file. \
    --This file is just full of text and numbers. It stores all the data necesary.
    function self.save(filemode)
        --The opened file
        local file = io.open(filemode, "w")
        file:write("Layers: " .. self.var.numLayers .. "\n")
        for l = 1, self.var.numLayers do
            file:write("Neurons: " .. self.var.numNeurons[l] .. "\n")
        end

        for l = 1, self.var.numLayers - 1 do
            for n1 = 1, self.var.numNeurons[l] do
                for n2 = 1, self.var.numNeurons[l + 1] do
                    file:write(self.var.weights[l][n1][n2] .. "\n")
                end
            end
        end
        file:flush()
        file:close()
    end
end

--Returns the colection
return new

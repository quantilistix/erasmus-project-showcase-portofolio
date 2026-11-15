function mod(x, y)
    return (x / y - math.floor(x / y)) * y
end
function rndmap(seed, legth)
    local map = {}
    for i = 0, length ^ 2 - 1 do
        seed = mod((1103515245 * seed + 12345), 2 ^ 31)
        map[i] = seed / 2 ^ 31
    end
    return map
end
function randommap(seed, length)
    math.randomseed(seed)
    local map = {}
    for i = 0, length ^ 2 - 1 do
        map[i] = math.random()
    end
    return map
end
seed = 1234
length = 100
map = randommap(seed, length)
teams = 2
bases = {}
for i = 0, teams - 1 do
    bases[i] = {}
end
bases[0][0] = {}
bases[1][0] = {}
bases[0][0].x = 2
bases[0][0].y = 2
bases[1][0].x = 100 - 4
bases[1][0].y = 100 - 8

rate = 0.6
for i = 0, length ^ 2 - 1 do
    if map[i] >= 1 - (1 - rate) / 4 then
        map[i] = 2
    else
        map[i] = math.floor(map[i] + rate)
    end
end
light = {}
for i = 0, length ^ 2 do
    light[i] = 0.05
end
x = 0
y = 0
base = love.graphics.newImage("Scattered_Barracks.png")
tree = love.graphics.newImage("Scattered_Tree.png")
function love.draw()
    for i = 0, length ^ 2 do
        if light[i] > 0.25 then
            light[i] = 0.5
        end
    end
    if love.keyboard.isDown("up") then
        y = y - 3.2
    end
    if love.keyboard.isDown("down") then
        y = y + 3.2
    end
    if love.keyboard.isDown("left") then
        x = x - 4
    end
    if love.keyboard.isDown("right") then
        x = x + 4
    end
    if x / 50 < -2 then
        x = -2 * 50
    end
    if y / 40 < -2 then
        y = -2 * 40
    end
    if x / 50 > length - 14 then
        x = (length - 14) * 50
    end
    if y / 40 > length - 10 then
        y = (length - 10) * 40
    end
    for i = math.floor(x / 50 + (y / 40 - 2) * length), math.floor(x / 50 + 17 + (y / 40 + 11) * length) - 1 do
        for j = 2, 13 do
            for k = 2, 9 do
                if
                    (j == 2 or j == 13 or k == 2 or k == 9) and
                        light[math.floor(x / 50 + 0.5 + j) + math.floor(y / 40 + 0.5 + k) * length] <= 0.25
                 then
                    light[math.floor(x / 50 + 0.5 + j) + math.floor(y / 40 + 0.5 + k) * length] = 0.25
                else
                    light[math.floor(x / 50 + 0.5 + j) + math.floor(y / 40 + 0.5 + k) * length] = 1
                end
            end
        end
        if i < 0 or i > length ^ 2 - 1 then
        else
            if map[i] == 2 then
                love.graphics.setColor(0, light[i], 0, 1)
                love.graphics.rectangle(
                    "fill",
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y,
                    50,
                    40 + 20
                )
                love.graphics.setColor(light[i], light[i], light[i], 1)
                love.graphics.draw(
                    tree,
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y - 20
                )

                love.graphics.setColor(light[i], light[i] / 3, 0, 0.5)
                love.graphics.rectangle(
                    "fill",
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y + 40,
                    50,
                    20
                )
            end
            if map[i] == 1 then
                love.graphics.setColor(0, light[i], 0, 1)
                love.graphics.rectangle(
                    "fill",
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y,
                    50,
                    40 + 20
                )
                love.graphics.setColor(light[i], light[i] / 3, 0, 0.5)
                love.graphics.rectangle(
                    "fill",
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y + 40,
                    50,
                    20
                )
            end
            if map[i] == 0 then
                love.graphics.setColor(light[i], light[i] / 3, 0, 0.25)
                love.graphics.rectangle(
                    "fill",
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y + 20,
                    50,
                    40
                )
                love.graphics.setColor(0, 0, light[i], 0.5)
                love.graphics.rectangle(
                    "fill",
                    (i - math.floor(i / length) * length) * 50 - x,
                    (math.floor(i / length)) * 40 - y + 10,
                    50,
                    40
                )
            end
        end
    end
    for i = 0, teams - 1 do
        for j = 0, #bases[i] do
            if light[bases[i][j].x + bases[i][j].y * length] == 1 then
                love.graphics.setColor(1, 1, 1, 1)
                love.graphics.draw(base, bases[i][j].x * 50 - x, bases[i][j].y * 40 - y - 20)
            end
        end
    end
    love.graphics.setColor(0.5, 0.5, 0.5, 1)
    love.graphics.rectangle("fill", 0, 450, 800, 150)
end

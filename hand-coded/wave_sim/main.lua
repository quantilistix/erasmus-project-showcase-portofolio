--Returns the sigmoid value of ```x```.
---@param x number
---@return number
function math.sig(x)
    return 1 / (1 + math.exp(-x))
end

local web = {}
web.new = require("web")
web:new(128, 128)

function love.update()
    web.s(1)
end

function love.keypressed(key)
    if key == "space" then
        web.r = not web.r
    end
end

function love.mousepressed(x, y, button, isTouch)
    if button == 1 and x < 256 and y < 256 and x > 0 and y > 0 then
        web.m[math.ceil(x / 2)][math.ceil(y / 2)].p = 256
    end
end

web.m[64][64].p = 16
web.m[64][65].p = 16
web.m[65][64].p = 16
web.m[65][65].p = 16
function love.draw()
    --web.d(2, "v", false, false, false)
    web.d(2, "t", false, false, false)
end

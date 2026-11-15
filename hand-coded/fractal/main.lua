c = {}
z = {}
cs = 1
function c_add(x, y)
    local z = {}
    z.r = x.r + y.r
    z.i = x.i + y.i
    return z
end

function c_times(x, y)
    local z = {}
    local p = {}
    p.r1 = x.r * y.r
    p.i1 = x.r * y.i
    p.i2 = x.i * y.r
    p.r2 = -x.i * y.i
    z.r = p.r1 + p.r2
    z.i = p.i2 + p.i1
    return z
end

function love.mousepressed(x, y, button, isTouch)
    click = click + button
end

function love.mousereleased(x, y, button, isTouch)
    click = click - button
end

click = 0
zp = {}
love.graphics.setColor(1, 1, 1, 1)
xres = 200
yres = 150
xs = -2
ys = -2
xz = 1.0001
yz = 1.0001
function love.draw()
    x2 = 0

    for x = xs, xs + 4 / xz, 4 / xres / xz do
        y2 = 0
        for y = ys, ys + 4 / yz, 4 / yres / xz do
            z.r = 0
            z.i = 0
            c.r = x
            c.i = y
            -- c.r = 0.25
            -- c.i = 0
            for i = 0, 1000 do
                z = c_add(c_times(z, z), c)
            end
            if math.sqrt(z.r ^ 2 + z.i ^ 2) > 0 then
                love.graphics.setColor(math.sqrt(z.r ^ 2 + z.i ^ 2), math.sqrt(z.r ^ 2 + z.i ^ 2), 1, 1)
            else
                love.graphics.setColor(0, 0, 0, 1)
            end
            love.graphics.rectangle("fill", x2 * (800 / xres), y2 * (600 / yres), (800 / xres), (600 / yres))
            y2 = y2 + 1
        end
        x2 = x2 + 1
    end
    love.graphics.setColor(1, 1, 1, 0.5)
    love.graphics.line(0, 300, 800, 300)
    love.graphics.line(400, 0, 400, 600)
end

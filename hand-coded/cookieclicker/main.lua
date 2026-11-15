cookie = love.graphics.newImage("Cookie.png")
small_cookie = love.graphics.newImage("Small_Cookie.png")
times_2 = love.graphics.newImage("2x.png")
big_factory = love.graphics.newImage("BigFactory.png")
cursor = love.graphics.newImage("Cursor.png")
factory = love.graphics.newImage("Factory.png")
house = love.graphics.newImage("House.png")
grandma = love.graphics.newImage("Grandma.png")
house_interior = love.graphics.newImage("HouseInterior.png")
power = love.graphics.newImage("Power.png")
power_tube = love.graphics.newImage("PowerTube.png")
small_grandma = love.graphics.newImage("Small_Grandma.png")
extra_cursor = love.graphics.newImage("ExtraCursor.png")
s = 1
click = false
cookies = 0
small_cookies = 10
cookie_effect = 0
cookie_effects = {}
click_power = 1
cookies_per_second = 0
for i = 0, small_cookies - 1 do
    cookie_effects[i] = {}
    cookie_effects[i][3] = -1
end
function love.mousepressed(x, y, button, isTouch)
    if button == 1 and x >= 400 - 32 * s and x <= 400 + 32 * s and y > 300 - 32 * s and y <= 300 + 32 * s then
        click = "cookie"
    end
    if button == 1 and x >= 0 and x <= 128 and y > 64 and y <= 128 then
        click = "cursor"
    end
    if button == 1 and x >= 0 and x <= 128 and y > 128 and y <= 64 * 3 then
        click = "grandma"
    end
    if button == 1 and x >= 0 and x <= 128 and y > 64 * 3 and y <= 256 then
        click = "house"
    end
    if button == 1 and x >= 0 and x <= 128 and y > 256 and y <= 64 * 5 then
        click = "factory"
    end
    if button == 1 and x >= 0 and x <= 128 and y > 64 * 5 and y <= 64 * 6 then
        click = "extra_cursor"
    end
end

t = os.time()
function love.draw()
    timer = os.time() - t
    if os.time() - t >= 1 then
        t = os.time()
        cookies = cookies + cookies_per_second
    end
    love.graphics.setColor(1, 1, 1, 1)
    love.graphics.print("Cookies:")
    love.graphics.print(cookies, 50, 0)
    love.graphics.print("Click power:", 0, 500)
    love.graphics.print(click_power, 75, 500)
    love.graphics.print("Cookies/s:", 0, 510)
    love.graphics.print(cookies_per_second, 75, 510)
    love.graphics.draw(cookie, 400 - 32 * s, 300 - 32 * s, 0, s, s)
    love.graphics.draw(cursor, 0, 64)
    love.graphics.draw(grandma, 0, 128)
    love.graphics.draw(house, 0, 64 * 3)
    love.graphics.draw(factory, 0, 256)
    love.graphics.draw(extra_cursor, 0, 64 * 5)
    if click == "cursor" and cookies >= 100 then
        cookies = cookies - 100
        click_power = click_power + 1
    end
    if click == "extra_cursor" and cookies >= 10000 then
        cookies = cookies - 10000
        click_power = click_power + 100
    end
    if click == "grandma" and cookies >= 1000 then
        cookies = cookies - 1000
        cookies_per_second = cookies_per_second + 1
    end
    if click == "factory" and cookies >= 10000000 then
        cookies = cookies - 10000000
        cookies_per_second = cookies_per_second + 10000
    end
    if s < 1 then
        s = s + 0.002
    end
    if click == "cookie" then
        s = 0.75
        cookies = cookies + click_power
        cookie_effect = cookie_effect + 1
        if cookie_effect > small_cookies then
            cookie_effect = 0
        end
        cookie_effects[cookie_effect] = { love.mouse.getX() - 32, love.mouse.getY(), 0 }
    end
    for i = 0, small_cookies - 1 do
        if cookie_effects[i][3] ~= -1 then
            love.graphics.setColor(1, 1, 1, 1 - cookie_effects[i][3])
            love.graphics.draw(
                small_cookie,
                cookie_effects[i][1],
                cookie_effects[i][2] - cookie_effects[i][3] * 16 - 32
            )
            cookie_effects[i][3] = cookie_effects[i][3] + 0.01
            if cookie_effects[i][3] >= 1 then
                cookie_effects[i][3] = -1
            end
        end
    end
    click = ""
end

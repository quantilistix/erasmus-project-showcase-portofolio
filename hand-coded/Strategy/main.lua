--Space bar active!
speed = 2
function love.load()
    --Load tables
    object = {}
    mountain_pos = {}
    order = {}

    --Load sprites
    soldier = love.graphics.newImage("Soldier.png")
    fire_ball = {}
    fire_ball[1] = love.graphics.newImage("FireBall.png")
    fire_ball[2] = love.graphics.newImage("FireBall_Boom1.png")
    fire_ball[3] = love.graphics.newImage("FireBall_Boom2.png")
    fire_ball[4] = love.graphics.newImage("FireBall_Boom3.png")
    tree = love.graphics.newImage("Brad.png")
    mountain = love.graphics.newImage("Munte.png")

    --Set graphics filter to nearest
    fire_ball[1]:setFilter("nearest", "nearest")
    fire_ball[2]:setFilter("nearest", "nearest")
    fire_ball[3]:setFilter("nearest", "nearest")
    fire_ball[4]:setFilter("nearest", "nearest")
    soldier:setFilter("nearest", "nearest")
    tree:setFilter("nearest", "nearest")
    mountain:setFilter("nearest", "nearest")

    --Set time
    time = 0

    --tree_map = love.filesystem.newFile("map.txt")
    --tree_map:open("r")

    --Set trees number
    object_count = 500

    --Set the enemy
    object[object_count + 2 + 100] = {}
    object[object_count + 2 + 100].type = "soldier"
    object[object_count + 2 + 100].light = { 0.9, 0.9, 0.9 }
    object[object_count + 2 + 100].x = love.math.random() * 10000
    object[object_count + 2 + 100].y = love.math.random() * 10000
    object[object_count + 2 + 100].xv = 0
    object[object_count + 2 + 100].yv = 0
    object[object_count + 2 + 100].alive = 1

    --tonumber((tree_map:read(1)))

    --Set pseudorandom seed
    love.math.setRandomSeed(1)

    --Set trees
    for r = 1, object_count do
        object[r] = {}
        --    tree_pos[r].x = tonumber((tree_map:read(17)))
        --    tree_pos[r].y = tonumber((tree_map:read(17)))
        if love.math.random() > 1 then
            object[r].type = "mountain"
        else
            object[r].type = "tree"
        end
        object[r].light = { 0.9, 0.9, 0.9 }
        object[r].x = love.math.random() * 10000
        object[r].y = love.math.random() * 10000
    end

    --Set fireballs
    for r = object_count + 1, object_count + 1 + 100 do
        object[r] = {}
        --    tree_pos[r].x = tonumber((tree_map:read(17)))
        --    tree_pos[r].y = tonumber((tree_map:read(17)))
        object[r].type = "fire_ball"
        object[r].light = { 1, 1, 1 }
        object[r].x = 1
        object[r].y = 1
        object[r].a = 0
        object[r].t = 0
        object[r].xv = 0
        object[r].yv = 0
    end

    --tree_map:close()

    --Set the camera settings
    FOV = 100
    viewplane = 1
    focal_length = 800
    r = 0
    x_player = 0
    y_player = 0
    for i = 0, #object do
        order[i] = i
    end
    fire_balls = 0
    fire_start = object_count + 1
    fire_end = object_count + 1 + 100
    click = 0
    enemy_start = object_count + 2 + 100
    enemy_end = object_count + 2 + 100
end

--Draw!
function love.draw()
    --Increase time
    time = time + 0.001

    --Make day and night
    c_time = (math.cos(time) + 1) / 1.1
    local b
    c = math.cos(math.rad(r))
    s = math.sin(math.rad(r))

    --Draw the sky and the floor
    love.graphics.setColor(0, 0.5 - c_time / (2 / 0.5), 1 - c_time / (2 / 1), 1)
    love.graphics.rectangle("fill", 0, 0, lua.window.getWidth, lua.window.getHeight / 2)
    love.graphics.setColor(0.9 - c_time / (2 / 0.9), 0.9 - c_time / (2 / 0.9), 0.9 - c_time / (2 / 0.9), 1)
    love.graphics.rectangle("fill", 0, lua.window.getHeight / 2, lua.window.getWidth, lua.window.getHeight / 2)

    --Set rendering order
    for i = 1, #object do
        object[i].yt = (-s * (object[order[i]].x + x_player) + c * (object[order[i]].y + y_player))
    end
    for i = 1, #object do
        for j = 1, #object do
            if object[j].yt < object[i].yt then
                b = object[j].yt
                object[j].yt = object[i].yt
                object[i].yt = b
                b = order[j]
                order[j] = order[i]
                order[i] = b
            end
        end
    end

    --Set lighting
    for i = 1, #object do
        if object[order[i]].type == "fire_ball" then
            object[order[i]].light = { 1, 1, 1 }
        else
            object[order[i]].light = { 0.9 - c_time / (2 / 0.9), 0.9 - c_time / (2 / 0.9), 0.9 - c_time / (2 / 0.9) }
        end

        --Calculate player colision with other objects
        xx = x_player - object[i].x
        yy = y_player - object[i].y
        d = math.sqrt(xx ^ 2 + yy ^ 2)
        if object[i].type == "tree" then
            if d < 150 then
                x_player = xx / d * 150 + object[i].x
                y_player = yy / d * 150 + object[i].y
            end
        elseif object[i].type == "mountain" then
            if d < 350 then
                x_player = xx / d * 350 + object[i].x
                y_player = yy / d * 350 + object[i].y
            end
        elseif object[i].type == "soldier" and object[i].alive == 1 then
            if d < 100 then
                x_player = xx / d * 100 + object[i].x
                y_player = yy / d * 100 + object[i].y
            end
        elseif object[i].type == "fire_ball" and object[i].a == 1 then
            if d < 90 then
                love.graphics.setColor(1, 0, 0, 1)
                love.graphics.print("GAME OVER", 0, 0, 0, 5, 5)
                love.graphics.present()
                abgah = 0
                while abgah <= 1000000 do
                    abgah = abgah + 0.001
                end
                love.event.quit()
            end
        end

        --Update fireball position
        if object[i].type == "fire_ball" and object[i].a == 1 then
            object[i].t = object[i].t + 1
            if object[i].t >= 1000 then
                object[i].a = object[i].a + 1
            end
            object[i].x = object[i].x + object[i].xv
            object[i].y = object[i].y + object[i].yv
        end

        --Fireball colosion and lighting
        for f = fire_start, fire_end do
            --Colision
            if object[f].a == 1 and f ~= i then
                xx = object[f].x - object[i].x
                yy = object[f].y - object[i].y
                d = math.sqrt(xx ^ 2 + yy ^ 2)
                if object[i].type == "tree" then
                    if d < 140 then
                        object[f].x = xx / d * 140 + object[i].x
                        object[f].y = yy / d * 140 + object[i].y
                        object[f].a = 2
                    end
                elseif object[i].type == "mountain" then
                    if d < 340 then
                        object[f].x = xx / d * 340 + object[i].x
                        object[f].y = yy / d * 340 + object[i].y
                        object[f].a = 2
                    end
                elseif object[i].type == "soldier" and object[i].alive == 1 then
                    if d < 90 then
                        object[f].x = xx / d * 90 + object[i].x
                        object[f].y = yy / d * 90 + object[i].y
                        object[f].a = 2
                        object[i].alive = 0
                    end
                elseif object[i].type == "fire_ball" and object[i].a == 1 and i ~= f then
                    if d < 80 then
                        object[f].x = xx / d * 80 + object[i].x
                        object[f].y = yy / d * 80 + object[i].y
                        object[f].a = 2
                    end
                end
            end
            --Lighting
            if object[order[i]].type ~= "fire_ball" and object[f].a ~= 0 then
                xx = object[f].x - object[order[i]].x
                yy = object[f].y - object[order[i]].y
                d = math.sqrt(xx ^ 2 + yy ^ 2)
                d = d * d
                if object[f].a > 1 then
                    object[order[i]].light[1] = object[order[i]].light[1] + 6800 / d / object[f].a * 3
                    object[order[i]].light[2] = object[order[i]].light[2] + 6800 / d / object[f].a * 3
                    object[order[i]].light[3] = object[order[i]].light[3] + 6800 / d / object[f].a * 3
                else
                    object[order[i]].light[1] = object[order[i]].light[1] + 6800 / d
                    object[order[i]].light[2] = object[order[i]].light[2] + 6800 / d
                    object[order[i]].light[3] = object[order[i]].light[3] + 6800 / d
                end
            end
        end

        --Caclulate enemy colision and dogde obstacles
        for f = enemy_start, enemy_end do
            --...if alive
            if object[f].alive == 1 and f ~= i then
                xx = object[f].x - object[i].x
                yy = object[f].y - object[i].y
                d = math.sqrt(xx ^ 2 + yy ^ 2)
                if object[i].type == "tree" then
                    object[f].x = object[f].x + xx / d * 0.0015
                    object[f].y = object[f].y + yy / d * 0.0015
                    if d < 150 then
                        object[f].x = xx / d * 150 + object[i].x
                        object[f].y = yy / d * 150 + object[i].y
                    end
                elseif object[i].type == "mountain" then
                    object[f].x = object[f].x + xx / d * 0.0035
                    object[f].y = object[f].y + yy / d * 0.0035
                    if d < 340 then
                        object[f].x = xx / d * 350 + object[i].x
                        object[f].y = yy / d * 350 + object[i].y
                    end
                elseif object[i].type == "soldier" and object[i].alive == 1 then
                    if d < 100 then
                        object[f].x = xx / d * 100 + object[i].x
                        object[f].y = yy / d * 100 + object[i].y
                    end
                elseif object[i].type == "fire_ball" and object[i].a == 1 then
                    object[f].x = object[f].x + xx / d
                    object[f].y = object[f].y + yy / d
                end
            end
        end

        --Update fireball explosion
        if object[i].type == "fire_ball" then
            if object[i].a > 1 then
                object[i].a = object[i].a + 0.1
            end
            if object[i].a >= 4.5 then
                object[i].a = 0
            end
        end

        --Update enemy position
        if object[i].type == "soldier" and object[i].alive == 1 then
            xx = object[i].x - x_player
            yy = object[i].y - y_player
            d = math.sqrt(xx ^ 2 + yy ^ 2)
            object[i].xv = -xx / d * speed
            object[i].yv = -yy / d * speed

            object[i].x = object[i].x + object[i].xv * 8 * speed
            object[i].y = object[i].y + object[i].yv * 8 * speed
            if love.math.random() > 0.99 then
                object[fire_balls + fire_start].a = 1
                object[fire_balls + fire_start].t = 0
                object[fire_balls + fire_start].xv = object[i].xv * 32
                object[fire_balls + fire_start].yv = object[i].yv * 32
                object[fire_balls + fire_start].x = object[i].x + object[i].xv * 100
                object[fire_balls + fire_start].y = object[i].y + object[i].yv * 100
                fire_balls = fire_balls + 1
                if fire_balls > fire_end - fire_start then
                    fire_balls = 1
                end
            end
        end

        --Set the light
        love.graphics.setColor(object[order[i]].light[1], object[order[i]].light[2], object[order[i]].light[3], 1)

        --Get the images' sizes
        if object[order[i]].type == "tree" then
            w = tree:getWidth()
            h = tree:getHeight()
        elseif object[order[i]].type == "mountain" then
            w = mountain:getWidth()
            h = mountain:getHeight() - (tree:getHeight() - mountain:getHeight())
        elseif object[order[i]].type == "soldier" then
            w = soldier:getWidth() / 1.5
            h = soldier:getHeight() / 1.5 - (tree:getHeight() - soldier:getHeight() / 1.5)
        elseif object[order[i]].type == "fire_ball" then
            w = fire_ball[1]:getWidth()
            h = fire_ball[1]:getHeight()
        end

        --Calculate rendering
        x = object[order[i]].x
        y = object[order[i]].y
        x_r = (c * (x - x_player) + s * (y - y_player))
        y_r = (-s * (x - x_player) + c * (y - y_player)) * math.tan(math.rad(FOV / 2)) * viewplane / focal_length
        x_sr = (x_r / y_r - w / y_r / 2 + lua.window.getWidth / 2)
        y_sr = (lua.window.getHeight / 2 - h / y_r / 2)

        --Render objects
        if y_r > 0 and x_sr > -w / y_r and x_sr < lua.window.getWidth then
            if object[order[i]].type == "tree" then
                love.graphics.draw(tree, x_sr, y_sr, 0, 1 / y_r, 1 / y_r)
            elseif object[order[i]].type == "mountain" then
                love.graphics.draw(mountain, x_sr, y_sr, 0, 1 / y_r, 1 / y_r)
            elseif object[order[i]].type == "soldier" and object[order[i]].alive == 1 then
                love.graphics.draw(soldier, x_sr, y_sr, 0, 1 / y_r / 1.5, 1 / y_r / 1.5)
            elseif object[order[i]].type == "fire_ball" then
                if object[order[i]].a > 0 then
                    love.graphics.draw(fire_ball[math.floor(object[order[i]].a)], x_sr, y_sr, 0, 1 / y_r, 1 / y_r)
                end
            end
        end
    end

    --Controls
    if love.keyboard.isDown("left") then
        r = r + 0.75 * speed
    end
    if love.keyboard.isDown("right") then
        r = r - 0.75 * speed
    end
    if love.keyboard.isDown("up") then
        x_player = x_player - math.sin(math.rad(r)) * 16 * speed
        y_player = y_player + math.cos(math.rad(r)) * 16 * speed
    end
    if love.keyboard.isDown("down") then
        x_player = x_player + math.sin(math.rad(r)) * 16 * speed
        y_player = y_player - math.cos(math.rad(r)) * 16 * speed
    end
    if love.keyboard.isDown("space") and click == 0 then
        click = 1
    end
    if love.keyboard.isDown("space") == false and click == 2 then
        click = 0
    end
    if click == 1 then
        object[fire_balls + fire_start].a = 1
        object[fire_balls + fire_start].t = 0
        object[fire_balls + fire_start].xv = -math.sin(math.rad(r)) * 32 * speed
        object[fire_balls + fire_start].yv = math.cos(math.rad(r)) * 32 * speed
        object[fire_balls + fire_start].x = x_player - math.sin(math.rad(r)) * 100
        object[fire_balls + fire_start].y = y_player + math.cos(math.rad(r)) * 100
        fire_balls = fire_balls + 1
        if fire_balls > fire_end - fire_start then
            fire_balls = 1
        end
    end
    if click == 1 then
        click = 2
    end
end

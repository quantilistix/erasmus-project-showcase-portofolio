--Loading up all the data
function love.load()
    --Functions
    --Check bounds function
    ---@param x integer
    ---@param y integer
    ---@param what string
    function check(x, y, what)
        if map[what][x] ~= nil then
            if map[what][x][y] ~= nil then
                return map[what][x][y]
            end
        end
        return nil
    end

    --Clears the previous valid moves
    function clear()
        for x = 1, 8 do
            for y = 1, 8 do
                map.s[x][y] = false
            end
        end
    end

    --Clears the dangers
    function clearD()
        for x = 1, 8 do
            for y = 1, 8 do
                map.d[x][y] = false
            end
        end
    end

    --Resets the dangers
    function resetD()
        clear()
        for x = 1, 8 do
            for y = 1, 8 do
                if map.t[x][y] == "w" then
                    check_check(x, y)
                end
            end
        end
        for x = 1, 8 do
            for y = 1, 8 do
                if map.s[x][y] then
                    map.d[x][y] = "w"
                end
            end
        end
        clear()
        for x = 1, 8 do
            for y = 1, 8 do
                if map.t[x][y] == "b" then
                    check_check(x, y)
                end
            end
        end
        for x = 1, 8 do
            for y = 1, 8 do
                if map.s[x][y] then
                    if map.d[x][y] == "w" then
                        map.d[x][y] = true
                    else
                        map.d[x][y] = "b"
                    end
                end
            end
        end
    end

    require("movement")
    require("check_checkmate_or_stalemate_detection")

    --Set filter
    love.graphics.setDefaultFilter("nearest", "nearest")
    --Create map
    map = {
        p = {
            { 4, 3, 2, 5, 6, 2, 3, 4 },
            { 1, 1, 1, 1, 1, 1, 1, 1 },
            { 0, 0, 0, 0, 0, 0, 0, 0 },
            { 0, 0, 0, 0, 0, 0, 0, 0 },
            { 0, 0, 0, 0, 0, 0, 0, 0 },
            { 0, 0, 0, 0, 0, 0, 0, 0 },
            { 1, 1, 1, 1, 1, 1, 1, 1 },
            { 4, 3, 2, 5, 6, 2, 3, 4 } },
        t = {
            { "b",   "b",   "b",   "b",   "b",   "b",   "b",   "b" },
            { "b",   "b",   "b",   "b",   "b",   "b",   "b",   "b" },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { "w",   "w",   "w",   "w",   "w",   "w",   "w",   "w" },
            { "w",   "w",   "w",   "w",   "w",   "w",   "w",   "w" } },
        s = {
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false } },
        d = {
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { "b",   "b",   "b",   "b",   "b",   "b",   "b",   "b" },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false },
            { "w",   "w",   "w",   "w",   "w",   "w",   "w",   "w" },
            { false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false } }
    }
    --Set turn
    turn = "w"
    --Load all sprites
    sprites = {}
    sprites[1] = love.graphics.newImage("pawn.png")
    sprites[2] = love.graphics.newImage("bishop.png")
    sprites[3] = love.graphics.newImage("knight.png")
    sprites[4] = love.graphics.newImage("rook.png")
    sprites[5] = love.graphics.newImage("queen.png")
    sprites[6] = love.graphics.newImage("king.png")
    --Repeat generating safety variable
    change = false
end

--Selection
function love.mousepressed(x, y, button, isTouch)
    --Check boundries
    if x / 64 > 1 and y / 64 > 1 and x / 64 < 9 and y / 64 < 9 and (button == 1 or isTouch) then
        --Select or move
        if not selectionX or not map.s[math.floor(y / 64)][math.floor(x / 64)] then
            selectionX = math.floor(y / 64)
            selectionY = math.floor(x / 64)
        else
            moveX = math.floor(y / 64)
            moveY = math.floor(x / 64)
        end

        --Set the safety off
        change = true
        --Clears previous valid moves
        clear()
    end
end

--The moving function
function move()
    --Move the piece
    local piece = map.p[moveX][moveY]
    local team = map.t[moveX][moveY]
    local timer = -1
    local queened = false
    map.p[moveX][moveY] = map.p[selectionX][selectionY]
    map.p[selectionX][selectionY] = 0
    map.t[moveX][moveY] = map.t[selectionX][selectionY]
    map.t[selectionX][selectionY] = false
    if map.t[moveX][moveY] == "w" and map.p[moveX][moveY] == 1 and moveX == 1 then
        love.graphics.setColor(1, .9, .8, 1)
        love.graphics.draw(sprites[5], 64, 0, 0, 4)
        love.graphics.draw(sprites[4], 128, 0, 0, 4)
        love.graphics.draw(sprites[3], 192, 0, 0, 4)
        love.graphics.draw(sprites[2], 256, 0, 0, 4)
        love.graphics.present()
        local loop = true
        local ascend
        while loop do
            if love.keyboard.isScancodeDown("q") then
                ascend = 5
                loop = false
            end
            if love.keyboard.isScancodeDown("r") then
                ascend = 4
                loop = false
            end
            if love.keyboard.isScancodeDown("k") then
                ascend = 3
                loop = false
            end
            if love.keyboard.isScancodeDown("b") then
                ascend = 2
                loop = false
            end
            love.timer.sleep(0.0001)
        end

        map.p[moveX][moveY] = ascend
        queened = true
    end
    if map.t[moveX][moveY] == "b" and map.p[moveX][moveY] == 1 and moveX == 8 then
        local loop = true
        local ascend
        while loop do
            if love.keyboard.isDown("q") then
                ascend = 5
                loop = false
            end
            if love.keyboard.isDown("r") then
                ascend = 4
                loop = false
            end
            if love.keyboard.isDown("k") then
                ascend = 3
                loop = false
            end
            if love.keyboard.isDown("b") then
                ascend = 2
                loop = false
            end
            love.draw()
            love.graphics.setColor(.4, .3, .2, 1)
            love.graphics.draw(sprites[5], 64, 0, 0, 4)
            love.graphics.draw(sprites[4], 128, 0, 0, 4)
            love.graphics.draw(sprites[3], 192, 0, 0, 4)
            love.graphics.draw(sprites[2], 256, 0, 0, 4)
            love.graphics.present()
        end

        map.p[moveX][moveY] = ascend
        queened = true
    end
    --Clears the dangers
    clearD()
    --Resets the dangers
    resetD()
    --Check for check
    for x = 1, 8 do
        for y = 1, 8 do
            if map.p[x][y] == 6 and (map.d[x][y] ~= turn and map.d[x][y] ~= false) then
                if map.t[x][y] == turn then
                    timer = 0
                else
                    timer = 1
                end
            end
        end
    end

    --Self check.
    if timer == 0 then
        if queened then
            map.p[selectionX][selectionY] = 1
            map.t[selectionX][selectionY] = map.t[moveX][moveY]
        else
            map.p[selectionX][selectionY] = map.p[moveX][moveY]
            map.t[selectionX][selectionY] = map.t[moveX][moveY]
        end
        map.p[moveX][moveY] = piece
        map.t[moveX][moveY] = team
        --Clears the dangers
        clearD()
        --Resets the dangers
        resetD()
        love.graphics.setColor(1, 1, 1, 1)
        love.graphics.print("Check!")
        love.graphics.present()
        love.timer.sleep(1)

        --Change turn
        if turn == "w" then
            turn = "b"
        else
            turn = "w"
        end
    end

    --Clear previous valid moves
    clear()

    --Check!
    if timer == 1 then
        --Redraw the board.
        love.draw()
        love.graphics.setColor(1, 1, 1, 1)
        love.graphics.print("Check!")
        love.graphics.present()
        love.timer.sleep(1)
    end

    --Clear selection
    moveX, moveY, selectionX, selectionY = false, false, false, false
    --Change turn
    if turn == "w" then
        turn = "b"
    else
        turn = "w"
    end
    change = false


    local stalemate = true
    local checkmate = true
    for x = 1, 8 do
        for y = 1, 8 do
            if map.d[x][y] == true or map.d[x][y] ~= turn then
                stalemate = false
            end
        end
    end
    if stalemate then
        Stalemate()
    end
end

--The updating function
function love.update()
    if selectionX and selectionY and change and not moveX and not moveY then
        if turn == map.t[selectionX][selectionY] then
            movement(selectionX, selectionY)
        end
    end

    if moveX and moveY and change then
        move()
    end
end

--The drawing function
function love.draw(check)
    for x = 1, 8 do
        love.graphics.setColor(1, 1, 1, 1)
        love.graphics.print(x, 584, 576 - x * 64)
        love.graphics.print(string.char(73 - x), 576 - x * 64, 584)
        for y = 1, 8 do
            love.graphics.setColor((x + y + 1) % 2 * .8, (x + y + 1) % 2 * .7 + .3, (x + y + 1) % 2 * .5 + .5, 1)
            love.graphics.rectangle("fill", y * 64, x * 64, 64, 64)
            if map.t[x][y] ~= false then
                if map.t[x][y] == "w" then
                    love.graphics.setColor(1, .9, .8, 1)
                else
                    love.graphics.setColor(.4, .3, .2, 1)
                end
                love.graphics.draw(sprites[map.p[x][y]], y * 64, x * 64, 0, 4)
            end
            if map.s[x][y] then
                love.graphics.setColor(0, 1, 0, 1)
                love.graphics.circle("fill", y * 64 + 32, x * 64 + 32, 8)
            end
        end
    end

    love.graphics.setColor(1, 1, 1, 1)
    love.graphics.rectangle("line", 64, 64, 512, 512)
    if selectionX and selectionY then
        love.graphics.setColor(0, 1, 0, .3)
        love.graphics.rectangle("fill", selectionY * 64, selectionX * 64, 64, 64)
    end
    if love.mouse.getX() / 64 > 1 and love.mouse.getY() / 64 > 1 and love.mouse.getX() / 64 < 9 and love.mouse.getY() / 64 < 9 then
        local color
        love.graphics.setColor(1, 1, 1, 1)
        if map.t[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == "w" then
            color = "White "
        elseif map.t[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == "b" then
            color = "Black "
        end
        if map.p[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == 1 then
            love.graphics.print(color .. "pawn", 600, 64 * 5)
        end
        if map.p[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == 2 then
            love.graphics.print(color .. "bishop", 600, 64 * 5)
        end
        if map.p[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == 3 then
            love.graphics.print(color .. "knight", 600, 64 * 5)
        end
        if map.p[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == 4 then
            love.graphics.print(color .. "rook", 600, 64 * 5)
        end
        if map.p[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == 5 then
            love.graphics.print(color .. "queen", 600, 64 * 5)
        end
        if map.p[math.floor(love.mouse.getY() / 64)][math.floor(love.mouse.getX() / 64)] == 6 then
            love.graphics.print(color .. "king", 600, 64 * 5)
        end
    end
end

--Generating valid moves
---@param x integer
---@param y integer
function check_check(x, y)
    --Pawn
    if map.p[x][y] == 1 then
        --White pawn
        if map.t[x][y] == "w" then
            --Attack
            if check(x - 1, y - 1, "p") then
                map.s[x - 1][y - 1] = true
            end
            if check(x - 1, y + 1, "p") then
                map.s[x - 1][y + 1] = true
            end
        end
        --Black pawn
        if map.t[x][y] == "b" then
            --Attack
            if check(x + 1, y - 1, "p") then
                map.s[x + 1][y - 1] = true
            end
            if check(x + 1, y + 1, "p") then
                map.s[x + 1][y + 1] = true
            end
        end
    end





    --Bishop
    if map.p[x][y] == 2 then
        local t = 1
        --White bishop
        if map.t[x][y] == "w" then
            while (check(x + t - 1, y + t - 1, "t") == false or (check(x + t - 1, y + t - 1, "t") == "b" and check(x + t - 1, y + t - 1, "p") == 6)) and check(x + t, y + t, "t") ~= nil do
                map.s[x + t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x + t - 1, y - t + 1, "t") == false or (check(x + t - 1, y - t + 1, "t") == "b" and check(x + t - 1, y - t + 1, "p") == 6)) and check(x + t, y - t, "t") ~= nil do
                map.s[x + t][y - t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t + 1, y + t - 1, "t") == false or (check(x - t + 1, y + t - 1, "t") == "b" and check(x - t + 1, y + t - 1, "p") == 6)) and check(x - t, y + t, "t") ~= nil do
                map.s[x - t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t + 1, y - t + 1, "t") == false or (check(x - t + 1, y - t + 1, "t") == "b" and check(x - t + 1, y - t + 1, "p") == 6)) and check(x - t, y - t, "t") ~= nil do
                map.s[x - t][y - t] = true
                t = t + 1
            end
        end
        --Black bishop
        if map.t[x][y] == "b" then
            while (check(x + t - 1, y + t - 1, "t") == false or (check(x + t - 1, y + t - 1, "t") == "w" and check(x + t - 1, y + t - 1, "p") == 6)) and check(x + t, y + t, "t") ~= nil do
                map.s[x + t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x + t - 1, y - t + 1, "t") == false or (check(x + t - 1, y - t + 1, "t") == "w" and check(x + t - 1, y - t + 1, "p") == 6)) and check(x + t, y - t, "t") ~= nil do
                map.s[x + t][y - t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t + 1, y + t - 1, "t") == false or (check(x - t + 1, y + t - 1, "t") == "w" and check(x - t + 1, y + t - 1, "p") == 6)) and check(x - t, y + t, "t") ~= nil do
                map.s[x - t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t + 1, y - t + 1, "t") == false or (check(x - t + 1, y - t + 1, "t") == "w" and check(x - t + 1, y - t + 1, "p") == 6)) and check(x - t, y - t, "t") ~= nil do
                map.s[x - t][y - t] = true
                t = t + 1
            end
        end
    end





    --Knight
    if map.p[x][y] == 3 then
        --White knight
        if map.t[x][y] == "w" then
            if check(x - 1, y - 2, "t") == "b" or check(x - 1, y - 2, "t") == false then
                map.s[x - 1][y - 2] = true
            end
            if check(x - 2, y - 1, "t") == "b" or check(x - 2, y - 1, "t") == false then
                map.s[x - 2][y - 1] = true
            end
            if check(x + 1, y - 2, "t") == "b" or check(x + 1, y - 2, "t") == false then
                map.s[x + 1][y - 2] = true
            end
            if check(x + 2, y - 1, "t") == "b" or check(x + 2, y - 1, "t") == false then
                map.s[x + 2][y - 1] = true
            end
            if check(x - 1, y + 2, "t") == "b" or check(x - 1, y + 2, "t") == false then
                map.s[x - 1][y + 2] = true
            end
            if check(x - 2, y + 1, "t") == "b" or check(x - 2, y + 1, "t") == false then
                map.s[x - 2][y + 1] = true
            end
            if check(x + 2, y + 1, "t") == "b" or check(x + 2, y + 1, "t") == false then
                map.s[x + 2][y + 1] = true
            end
            if check(x + 1, y + 2, "t") == "b" or check(x + 1, y + 2, "t") == false then
                map.s[x + 1][y + 2] = true
            end
        end
        --Black knight ()
        if map.t[x][y] == "b" then
            if check(x - 1, y - 2, "t") == "w" or check(x - 1, y - 2, "t") == false then
                map.s[x - 1][y - 2] = true
            end
            if check(x - 2, y - 1, "t") == "w" or check(x - 2, y - 1, "t") == false then
                map.s[x - 2][y - 1] = true
            end
            if check(x + 1, y - 2, "t") == "w" or check(x + 1, y - 2, "t") == false then
                map.s[x + 1][y - 2] = true
            end
            if check(x + 2, y - 1, "t") == "w" or check(x + 2, y - 1, "t") == false then
                map.s[x + 2][y - 1] = true
            end
            if check(x - 1, y + 2, "t") == "w" or check(x - 1, y + 2, "t") == false then
                map.s[x - 1][y + 2] = true
            end
            if check(x - 2, y + 1, "t") == "w" or check(x - 2, y + 1, "t") == false then
                map.s[x - 2][y + 1] = true
            end
            if check(x + 2, y + 1, "t") == "w" or check(x + 2, y + 1, "t") == false then
                map.s[x + 2][y + 1] = true
            end
            if check(x + 1, y + 2, "t") == "w" or check(x + 1, y + 2, "t") == false then
                map.s[x + 1][y + 2] = true
            end
        end
    end





    --Rook
    if map.p[x][y] == 4 then
        local t = 1
        --White rook
        if map.t[x][y] == "w" then
            while (check(x + t, y, "t") ~= "w" and check(x + t - 1, y, "t") ~= "b") and check(x + t, y, "t") ~= nil do
                map.s[x + t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y, "t") ~= "w" and check(x - t + 1, y, "t") ~= "b") and check(x - t, y, "t") ~= nil do
                map.s[x - t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x, y + t, "t") ~= "w" and check(x, y + t - 1, "t") ~= "b") and check(x, y + t, "t") ~= nil do
                map.s[x][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x, y - t, "t") ~= "w" and check(x, y - t + 1, "t") ~= "b") and check(x, y - t, "t") ~= nil do
                map.s[x][y - t] = true
                t = t + 1
            end
        end
        --Black rook
        if map.t[x][y] == "b" then
            while (check(x + t, y, "t") ~= "b" and check(x + t - 1, y, "t") ~= "w") and check(x + t, y, "t") ~= nil do
                map.s[x + t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y, "t") ~= "b" and check(x - t + 1, y, "t") ~= "w") and check(x - t, y, "t") ~= nil do
                map.s[x - t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x, y + t, "t") ~= "b" and check(x, y + t - 1, "t") ~= "w") and check(x, y + t, "t") ~= nil do
                map.s[x][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x, y - t, "t") ~= "b" and check(x, y - t + 1, "t") ~= "w") and check(x, y - t, "t") ~= nil do
                map.s[x][y - t] = true
                t = t + 1
            end
        end
    end





    --Queen
    if map.p[x][y] == 5 then
        local t = 1
        --White queen
        if map.t[x][y] == "w" then
            while (check(x + t, y + t, "t") ~= "w" and check(x + t - 1, y + t - 1, "t") ~= "b") and check(x + t, y + t, "t") ~= nil do
                map.s[x + t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x + t, y - t, "t") ~= "w" and check(x + t - 1, y - t + 1, "t") ~= "b") and check(x + t, y - t, "t") ~= nil do
                map.s[x + t][y - t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y + t, "t") ~= "w" and check(x - t + 1, y + t - 1, "t") ~= "b") and check(x - t, y + t, "t") ~= nil do
                map.s[x - t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y - t, "t") ~= "w" and check(x - t + 1, y - t + 1, "t") ~= "b") and check(x - t, y - t, "t") ~= nil do
                map.s[x - t][y - t] = true
                t = t + 1
            end
            t = 1
            while (check(x + t, y, "t") ~= "w" and check(x + t - 1, y, "t") ~= "b") and check(x + t, y, "t") ~= nil do
                map.s[x + t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y, "t") ~= "w" and check(x - t + 1, y, "t") ~= "b") and check(x - t, y, "t") ~= nil do
                map.s[x - t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x, y + t, "t") ~= "w" and check(x, y + t - 1, "t") ~= "b") and check(x, y + t, "t") ~= nil do
                map.s[x][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x, y - t, "t") ~= "w" and check(x, y - t + 1, "t") ~= "b") and check(x, y - t, "t") ~= nil do
                map.s[x][y - t] = true
                t = t + 1
            end
        end
        --Black queen
        if map.t[x][y] == "b" then
            while (check(x + t, y + t, "t") ~= "b" and check(x + t - 1, y + t - 1, "t") ~= "w") and check(x + t, y + t, "t") ~= nil do
                map.s[x + t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x + t, y - t, "t") ~= "b" and check(x + t - 1, y - t + 1, "t") ~= "w") and check(x + t, y - t, "t") ~= nil do
                map.s[x + t][y - t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y + t, "t") ~= "b" and check(x - t + 1, y + t - 1, "t") ~= "w") and check(x - t, y + t, "t") ~= nil do
                map.s[x - t][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y - t, "t") ~= "b" and check(x - t + 1, y - t + 1, "t") ~= "w") and check(x - t, y - t, "t") ~= nil do
                map.s[x - t][y - t] = true
                t = t + 1
            end
            t = 1
            while (check(x + t, y, "t") ~= "b" and check(x + t - 1, y, "t") ~= "w") and check(x + t, y, "t") ~= nil do
                map.s[x + t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x - t, y, "t") ~= "b" and check(x - t + 1, y, "t") ~= "w") and check(x - t, y, "t") ~= nil do
                map.s[x - t][y] = true
                t = t + 1
            end
            t = 1
            while (check(x, y + t, "t") ~= "b" and check(x, y + t - 1, "t") ~= "w") and check(x, y + t, "t") ~= nil do
                map.s[x][y + t] = true
                t = t + 1
            end
            t = 1
            while (check(x, y - t, "t") ~= "b" and check(x, y - t + 1, "t") ~= "w") and check(x, y - t, "t") ~= nil do
                map.s[x][y - t] = true
                t = t + 1
            end
        end
    end





    --King
    if map.p[x][y] == 6 then
        --White king
        if map.t[x][y] == "w" then
            if (check(x - 1, y - 1, "t") == "b" or check(x - 1, y - 1, "t") == false) and (check(x - 1, y - 1, "d") == false or check(x - 1, y - 1, "d") == "w") then
                map.s[x - 1][y - 1] = true
            end
            if (check(x, y - 1, "t") == "b" or check(x, y - 1, "t") == false) and (check(x, y - 1, "d") == false or check(x, y - 1, "d") == "w") then
                map.s[x][y - 1] = true
            end
            if (check(x + 1, y - 1, "t") == "b" or check(x + 1, y - 1, "t") == false) and (check(x + 1, y - 1, "d") == false or check(x + 1, y - 1, "d") == "w") then
                map.s[x + 1][y - 1] = true
            end
            if (check(x - 1, y, "t") == "b" or check(x - 1, y, "t") == false) and (check(x - 1, y, "d") == false or check(x - 1, y, "d") == "w") then
                map.s[x - 1][y] = true
            end
            if (check(x + 1, y, "t") == "b" or check(x + 1, y, "t") == false) and (check(x + 1, y, "d") == false or check(x + 1, y, "d") == "w") then
                map.s[x + 1][y] = true
            end
            if (check(x - 1, y + 1, "t") == "b" or check(x - 1, y + 1, "t") == false) and (check(x - 1, y + 1, "d") == false or check(x - 1, y + 1, "d") == "w") then
                map.s[x - 1][y + 1] = true
            end
            if (check(x, y + 1, "t") == "b" or check(x, y + 1, "t") == false) and (check(x, y + 1, "d") == false or check(x, y + 1, "d") == "w") then
                map.s[x][y + 1] = true
            end
            if (check(x + 1, y + 1, "t") == "b" or check(x + 1, y + 1, "t") == false) and (check(x + 1, y + 1, "d") == false or check(x + 1, y + 1, "d") == "w") then
                map.s[x + 1][y + 1] = true
            end
        end
        --Black king
        if map.t[x][y] == "b" then
            if (check(x - 1, y - 1, "t") == "w" or check(x - 1, y - 1, "t") == false) and (check(x - 1, y - 1, "d") == false or check(x - 1, y - 1, "d") == "b") then
                map.s[x - 1][y - 1] = true
            end
            if (check(x, y - 1, "t") == "w" or check(x, y - 1, "t") == false) and (check(x, y - 1, "d") == false or check(x, y - 1, "d") == "b") then
                map.s[x][y - 1] = true
            end
            if (check(x + 1, y - 1, "t") == "w" or check(x + 1, y - 1, "t") == false) and (check(x + 1, y - 1, "d") == false or check(x + 1, y - 1, "d") == "b") then
                map.s[x + 1][y - 1] = true
            end
            if (check(x - 1, y, "t") == "w" or check(x - 1, y, "t") == false) and (check(x - 1, y, "d") == false or check(x - 1, y, "d") == "b") then
                map.s[x - 1][y] = true
            end
            if (check(x + 1, y, "t") == "w" or check(x + 1, y, "t") == false) and (check(x + 1, y, "d") == false or check(x + 1, y, "d") == "b") then
                map.s[x + 1][y] = true
            end
            if (check(x - 1, y + 1, "t") == "w" or check(x - 1, y + 1, "t") == false) and (check(x - 1, y + 1, "d") == false or check(x - 1, y + 1, "d") == "b") then
                map.s[x - 1][y + 1] = true
            end
            if (check(x, y + 1, "t") == "w" or check(x, y + 1, "t") == false) and (check(x, y + 1, "d") == false or check(x, y + 1, "d") == "b") then
                map.s[x][y + 1] = true
            end
            if (check(x + 1, y + 1, "t") == "w" or check(x + 1, y + 1, "t") == false) and (check(x + 1, y + 1, "d") == false or check(x + 1, y + 1, "d") == "b") then
                map.s[x + 1][y + 1] = true
            end
        end
    end
    change = false
end

function Stalemate()
    function love.update()
    end

    function love.draw()
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
        love.graphics.print("Stalemate (1/2 1/2)")
    end
end

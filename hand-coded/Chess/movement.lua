--Generating valid moves
---@param x integer
---@param y integer
function movement(x, y)
    --Pawn
    if map.p[x][y] == 1 then
        --White pawn
        if map.t[x][y] == "w" then
            --Move 2 or 1
            if x == 7 then
                if map.p[6][y] == 0 then
                    map.s[6][y] = true
                    if map.p[5][y] == 0 then
                        map.s[5][y] = true
                    end
                end
            else
                if map.p[x - 1][y] == 0 then
                    map.s[x - 1][y] = true
                end
            end
            --Attack
            if check(x - 1, y - 1, "p") ~= 0 and check(x - 1, y - 1, "t") == "b" then
                map.s[x - 1][y - 1] = true
            end
            if check(x - 1, y + 1, "p") ~= 0 and check(x - 1, y + 1, "t") == "b" then
                map.s[x - 1][y + 1] = true
            end
        end
        --Black pawn
        if map.t[x][y] == "b" then
            --Move 2 or 1
            if x == 2 then
                if map.p[3][y] == 0 then
                    map.s[3][y] = true
                    if map.p[4][y] == 0 then
                        map.s[4][y] = true
                    end
                end
            else
                if map.p[x + 1][y] == 0 then
                    map.s[x + 1][y] = true
                end
            end
            --Attack
            if check(x + 1, y - 1, "p") ~= 0 and check(x + 1, y - 1, "t") == "w" then
                map.s[x + 1][y - 1] = true
            end
            if check(x + 1, y + 1, "p") ~= 0 and check(x + 1, y + 1, "t") == "w" then
                map.s[x + 1][y + 1] = true
            end
        end
    end





    --Bishop
    if map.p[x][y] == 2 then
        local t = 1
        --White bishop
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
        end
        --Black bishop
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

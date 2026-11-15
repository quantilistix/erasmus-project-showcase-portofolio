--cast a 2d ray

function cast(v1, v2, out, obstacles)
    --local vars
    local t, u, p1, p2, px, py, safe, t2, u2, d, db
    px = 0
    py = 0
    d = 0
    db = -1

    --get pos amplifiers
    for i = 1, obstacles do
        t =
            ((v1.x1 - v2[i].x1) * (v2[i].y1 - v2[i].y2) - (v1.y1 - v2[i].y1) * (v2[i].x1 - v2[i].x2)) /
            ((v1.x1 - v1.x2) * (v2[i].y1 - v2[i].y2) - (v1.y1 - v1.y2) * (v2[i].x1 - v2[i].x2))
        u =
            ((v1.x1 - v2[i].x1) * (v1.y1 - v1.y2) - (v1.y1 - v2[i].y1) * (v1.x1 - v1.x2)) /
            ((v1.x1 - v1.x2) * (v2[i].y1 - v2[i].y2) - (v1.y1 - v1.y2) * (v2[i].x1 - v2[i].x2))

        --calculate the intersection
        if 0 <= t and (0 <= u and u <= 1) then
            p1 = v1.x1 + t * (v1.x2 - v1.x1)
            p2 = v1.y1 + t * (v1.y2 - v1.y1)
            d = math.sqrt((v1.x1 - p1) ^ 2 + (v1.y1 - p2) ^ 2)
            if d < db or db == -1 then
                db = d
                px = p1
                py = p2
                t2 = t
                u2 = u
            end
        end
        --draw obstacles
        love.graphics.line(v2[i].x1, v2[i].y1, v2[i].x2, v2[i].y2)
    end
    if t2 then
        t = t2
    end
    if u2 then
        u = u2
    end
    --draw rays and get distance
    if 0 <= t and (0 <= u and u <= 1) then
        love.graphics.line(v1.x1, v1.y1, p1, p2)
        out.d = math.sqrt((v1.x1 - px) ^ 2 + (v1.y1 - py) ^ 2)
    else
        love.graphics.line(v1.x1, v1.y1, v1.x2, v1.y2)
        out.d = 0
    end

    --publish the intersection point
    out.x = px
    out.y = py
end

l2 = {}
l3 = {}
l1 = {}
o = {}
l2.x1 = 101.001
l2.y1 = 101.001
l2.x2 = 201.001
l2.y2 = 101.001
l3.x1 = 101.001
l3.y1 = 101.001
l3.x2 = 101.001
l3.y2 = 201.001
l1.x1 = 0
l1.y1 = 0
l1.x2 = math.cos(0) * 1000
l1.y2 = math.sin(0) * 1000
l1.r = 45
o.x = 0
o.y = 0
o.d = 0
j = 0
l2[1] = l3
l2[2] = l2
function lit()
    j = 0
    for i = -math.pi / 4 + math.rad(l1.r), math.pi / 4 + math.rad(l1.r), math.pi / 100 do
        l1.x2 = math.cos(i) * 1000 + l1.x1
        l1.y2 = math.sin(i) * 1000 + l1.y1

        cast(l1, l2, o, 2)
        --if o.d > 10 then
        --    love.graphics.line(j * 100, -200 / (o.d / 20) + 300, j * 100, 200 / (o.d / 20) + 300)
        --end

        j = j + 2 / 400
    end
end

function love.draw()
    s = math.sin(math.rad(l1.r))
    c = math.cos(math.rad(l1.r))

    if love.keyboard.isDown("up") == true then
        l1.x1 = l1.x1 + c
        l1.x2 = l1.x2 + c
        l1.y1 = l1.y1 + s
        l1.y2 = l1.y2 + s
    end

    if love.keyboard.isDown("down") == true then
        l1.x1 = l1.x1 - c
        l1.x2 = l1.x2 - c
        l1.y1 = l1.y1 - s
        l1.y2 = l1.y2 - s
    end

    if love.keyboard.isDown("x") == true then
        l1.x1 = l1.x1 - s
        l1.x2 = l1.x2 - s
        l1.y1 = l1.y1 + c
        l1.y2 = l1.y2 + c
    end

    if love.keyboard.isDown("z") == true then
        l1.x1 = l1.x1 + s
        l1.x2 = l1.x2 + s
        l1.y1 = l1.y1 - c
        l1.y2 = l1.y2 - c
    end

    if love.keyboard.isDown("left") == true then
        l1.r = l1.r - 1
    end

    if love.keyboard.isDown("right") == true then
        l1.r = l1.r + 1
    end
    lit()
end

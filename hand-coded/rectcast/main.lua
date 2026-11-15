--this works only for grid surfaces
function rectcast(ray, obs)
    local t, u, px, py, res, p2, p2, d, dist, u2, t2
    d = -1
    t2 = -1
    u2 = -1
    for i = 0, obs.obs - 1 do
        if obs[i].way == 0 then
            t = (obs[i].x1 - ray.x1) / (ray.x2 - ray.x1)
            px = ray.x1 + t * (ray.x2 - ray.x1)
            py = ray.y1 + t * (ray.y2 - ray.y1)
            u = (py - obs[i].y1) / (obs[i].y2 - obs[i].y1)
        else
            t = (obs[i].y1 - ray.y1) / (ray.y2 - ray.y1)
            py = ray.y1 + t * (ray.y2 - ray.y1)
            px = ray.x1 + t * (ray.x2 - ray.x1)
            u = (py - obs[i].x1) / (obs[i].x2 - obs[i].x1)
        end
        --        love.graphics.line(obs[i].x1, obs[i].y1, obs[i].x2, obs[i].y2)
        if t >= 0 and u >= 0 and u <= 1 then
            dist = math.sqrt((ray.x1 - px) ^ 2 + (ray.y1 - py) ^ 2)
            if d == -1 or dist < d then
                d = dist
                res = i
                p1 = px
                p2 = py
                t2 = t
                u2 = u
            end
        end
    end
    --    love.graphics.line(ray.x1, ray.y1, ray.x2, ray.y2)
    if t2 >= 0 and u2 >= 0 and u2 <= 1 then
        --        love.graphics.circle("line", p1, p2, 5)
        return d, u2 / obs[res].l
    else
        return nil
    end
end
love.graphics.setColor(1, 1, 1, 1)
pi = math.pi
ray = {}
ray.x1 = 0
ray.y1 = 0
ray.x2 = 100
ray.y2 = 100
obs1 = {}
obs1.x1 = 50
obs1.y1 = 0
obs1.x2 = 50
obs1.y2 = 100
obs1.l = 1
obs2 = {}
obs2.x1 = 0
obs2.y1 = 50
obs2.x2 = 100
obs2.y2 = 50
obs2.l = 1
obs = {}
obs.obs = 2
obs[0] = obs1
obs[1] = obs2
obs[0].way = 0
obs[1].way = 1
angle = math.rad(45)
function love.draw()
    for i = -1, 1, 0.001 do
        ray.x2 = math.cos(i * pi / 4 + angle) * 1000 + ray.x1
        ray.y2 = math.sin(i * pi / 4 + angle) * 1000 + ray.y1
        d, u = rectcast(ray, obs)
        if d then
            love.graphics.setColor(u, u, u, 1)
            love.graphics.line(i * 800, -200 / d * 100 + 300, i * 800, 200 / d * 100 + 300)
        end
    end
    if love.keyboard.isDown("left") then
        angle = angle - pi / 500
    end
    if love.keyboard.isDown("right") then
        angle = angle + pi / 500
    end
    if love.keyboard.isDown("up") then
        ray.x1 = ray.x1 + math.cos(angle)
        ray.y1 = ray.y1 + math.sin(angle)
    end
    if love.keyboard.isDown("down") then
        ray.x1 = ray.x1 - math.cos(angle)
        ray.y1 = ray.y1 - math.sin(angle)
    end
    if love.keyboard.isDown("z") then
        ray.x1 = ray.x1 + math.sin(angle)
        ray.y1 = ray.y1 - math.cos(angle)
    end
    if love.keyboard.isDown("x") then
        ray.x1 = ray.x1 - math.sin(angle)
        ray.y1 = ray.y1 + math.cos(angle)
    end
end

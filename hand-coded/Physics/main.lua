Mesh = {}
Mesh.new = require("mesh")
local vtx = {}
local fac = {}
local lines = {}
local k = 1000
local d = 5
local f = .1
local function init_mesh()
    vtx = {
        { x = -1, y = -1, z = 11, m = 3, xv = 0,  yv = 0, zv = 0 },
        { x = -1, y = -1, z = 9,  m = 3, xv = 10, yv = 0, zv = 0 },
        { x = 1,  y = -1, z = 11, m = 3, xv = 0,  yv = 0, zv = 0 },
        { x = 1,  y = -1, z = 9,  m = 3, xv = 0,  yv = 0, zv = 0 },
        { x = -1, y = 1,  z = 11, m = 3, xv = 0,  yv = 0, zv = 0 },
        { x = -1, y = 1,  z = 9,  m = 3, xv = 0,  yv = 0, zv = 0 },
        { x = 1,  y = 1,  z = 11, m = 3, xv = 0,  yv = 0, zv = 0 },
        { x = 1,  y = 1,  z = 9,  m = 3, xv = 0,  yv = 0, zv = 0 } }

    fac = {
        { 1, 2, 3, f = 1, d = 1 },
        { 4, 2, 3, f = 1, d = 1 },
        { 1, 6, 5, f = 1, d = 1 },
        { 6, 1, 2, f = 1, d = 1 },
        { 8, 5, 6, f = 1, d = 1 },
        { 8, 5, 7, f = 1, d = 1 },
        { 8, 7, 3, f = 1, d = 1 },
        { 8, 3, 4, f = 1, d = 1 },
        { 5, 7, 3, f = 1, d = 1 },
        { 5, 3, 1, f = 1, d = 1 },
        { 6, 2, 4, f = 1, d = 1 },
        { 6, 4, 8, f = 1, d = 1 } }
    lines = {
        { 1, 2, k = k, d = d, f = f },
        { 1, 3, k = k, d = d, f = f },
        { 2, 4, k = k, d = d, f = f },
        { 3, 4, k = k, d = d, f = f },
        { 1, 4, k = k, d = d, f = f },
        { 2, 3, k = k, d = d, f = f },
        { 5, 6, k = k, d = d, f = f },
        { 5, 7, k = k, d = d, f = f },
        { 6, 8, k = k, d = d, f = f },
        { 7, 8, k = k, d = d, f = f },
        { 5, 8, k = k, d = d, f = f },
        { 6, 7, k = k, d = d, f = f },
        { 1, 5, k = k, d = d, f = f },
        { 2, 6, k = k, d = d, f = f },
        { 3, 7, k = k, d = d, f = f },
        { 4, 8, k = k, d = d, f = f },
        { 1, 6, k = k, d = d, f = f },
        { 2, 5, k = k, d = d, f = f },
        { 3, 8, k = k, d = d, f = f },
        { 4, 7, k = k, d = d, f = f },
        { 1, 7, k = k, d = d, f = f },
        { 1, 8, k = k, d = d, f = f },
        { 2, 7, k = k, d = d, f = f },
        { 1, 8, k = k, d = d, f = f },
        { 3, 5, k = k, d = d, f = f },
        { 3, 6, k = k, d = d, f = f },
        { 4, 5, k = k, d = d, f = f },
        { 4, 6, k = k, d = d, f = f },
        { 2, 8, k = k, d = d, f = f } }
end
init_mesh()
Mesh:new(vtx, lines, fac)

function love.update(dt)
    Mesh.s(false, dt)
    if love.keyboard.isDown("space") then
        init_mesh()
        Mesh:new(vtx, lines, fac)
    end
end

function love.draw()
    Mesh.d()
    love.graphics.setColor(1, 1, 1, 1)
    love.graphics.print("Stiffness: " .. k .. "N/m", 0, 0)
    love.graphics.print("Damping: " .. d .. "Ns/m", 0, 15)
end

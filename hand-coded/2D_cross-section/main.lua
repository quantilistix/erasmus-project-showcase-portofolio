--Returns the signum of the value ```x```
function math.sgn(x)
    return x / math.abs(x)
end

Cube = {}
Cube.new = require("3D_object")
local vtx = { { x = -1, y = -1, z = -1 },
    { x = 1,  y = -1, z = -1 },
    { x = -1, y = 1,  z = -1 },
    { x = -1, y = -1, z = 1 },
    { x = 1,  y = 1,  z = -1 },
    { x = -1, y = 1,  z = 1 },
    { x = 1,  y = -1, z = 1 },
    { x = 1,  y = 1,  z = 1 } }
local faces = { { 1, 2, 3 },
    { 5, 2, 3 },
    { 4, 6, 7 },
    { 6, 7, 8 },
    { 1, 2, 4 },
    { 2, 4, 7 },
    { 3, 5, 6 },
    { 5, 6, 8 },
    { 1, 3, 4 },
    { 3, 4, 6 },
    { 2, 5, 7 },
    { 5, 7, 8 } }
Cube:new(vtx, faces)
Cube.edit.rot(45, 35, 30)
Cube.edit.move(0, -2.5, 0)
function love.draw()
    Cube.debug()
    --Cube.edit.rot(1, 1, 1)
    Cube.edit.move(0, 0.01, 0)
end

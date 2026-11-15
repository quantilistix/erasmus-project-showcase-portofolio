--Make a new mesh.
---@param vtx table
---@param fac table
---@param lines table
---@param self table
---@class soft_body
local function new(self, vtx, lines, fac)
    --The vertices table.
    self.v = vtx
    --Set velocities.
    for i = 1, #self.v do
        self.v[i].xv = self.v[i].xv or 0
        self.v[i].yv = self.v[i].yv or 0
        self.v[i].zv = self.v[i].zv or 0
        self.v[i].fx = 0
        self.v[i].fy = 0
        self.v[i].fz = 0
    end
    --The faces table.
    self.f = fac
    --The springs table.
    self.l = lines
    for i = 1, #self.l do
        self.l[i].l = self.l[i].l or
            math.sqrt((self.v[self.l[i][2]].x - self.v[self.l[i][1]].x) ^ 2 +
                (self.v[self.l[i][2]].y - self.v[self.l[i][1]].y) ^ 2 +
                (self.v[self.l[i][2]].z - self.v[self.l[i][1]].z) ^ 2)
    end

    --Drawing the mesh.                                  --#Draw#--
    function self.d()
        --The height and width of the screen.
        local height, width = love.graphics.getHeight(), love.graphics.getWidth()
        local FOV = math.tan(math.rad(80)) / width
        love.graphics.setColor(0.2, 1, 0.2, 1)
        love.graphics.rectangle("fill", 0, height / 2, width, height)
        love.graphics.setColor(0, 0.5, 1, 1)
        love.graphics.rectangle("fill", 0, 0, width, height / 2)
        love.graphics.setColor(0.2, 0.7, 0.2, .3)
        for i = 1, #self.f do
            love.graphics.polygon("fill",
                width / 2 + self.v[self.f[i][1]].x / ((self.v[self.f[i][1]].y + 10) * FOV),
                height / 2 - (-5) / ((self.v[self.f[i][1]].y + 10) * FOV),
                width / 2 + self.v[self.f[i][2]].x / ((self.v[self.f[i][2]].y + 10) * FOV),
                height / 2 - (-5) / ((self.v[self.f[i][2]].y + 10) * FOV),
                width / 2 + self.v[self.f[i][3]].x / ((self.v[self.f[i][3]].y + 10) * FOV),
                height / 2 - (-5) / ((self.v[self.f[i][3]].y + 10) * FOV))
        end
        love.graphics.setColor(0.2, 0.7, 0.2, 1)
        for i = 1, #self.l do
            love.graphics.line(
                width / 2 + self.v[self.l[i][1]].x / ((self.v[self.l[i][1]].y + 10) * FOV),
                height / 2 - (-5) / ((self.v[self.l[i][1]].y + 10) * FOV),
                width / 2 + self.v[self.l[i][2]].x / ((self.v[self.l[i][2]].y + 10) * FOV),
                height / 2 - (-5) / ((self.v[self.l[i][2]].y + 10) * FOV))
        end
        love.graphics.setColor(0.5, 0.3, 0, .3)
        for i = 1, #self.f do
            love.graphics.polygon("fill",
                width / 2 + self.v[self.f[i][1]].x / ((self.v[self.f[i][1]].y + 10) * FOV),
                height / 2 - (self.v[self.f[i][1]].z - 5) / ((self.v[self.f[i][1]].y + 10) * FOV),
                width / 2 + self.v[self.f[i][2]].x / ((self.v[self.f[i][2]].y + 10) * FOV),
                height / 2 - (self.v[self.f[i][2]].z - 5) / ((self.v[self.f[i][2]].y + 10) * FOV),
                width / 2 + self.v[self.f[i][3]].x / ((self.v[self.f[i][3]].y + 10) * FOV),
                height / 2 - (self.v[self.f[i][3]].z - 5) / ((self.v[self.f[i][3]].y + 10) * FOV))
        end
        love.graphics.setColor(0.5, 0.3, 0, 1)
        for i = 1, #self.l do
            love.graphics.line(
                width / 2 + self.v[self.l[i][1]].x / ((self.v[self.l[i][1]].y + 10) * FOV),
                height / 2 - (self.v[self.l[i][1]].z - 5) / ((self.v[self.l[i][1]].y + 10) * FOV),
                width / 2 + self.v[self.l[i][2]].x / ((self.v[self.l[i][2]].y + 10) * FOV),
                height / 2 - (self.v[self.l[i][2]].z - 5) / ((self.v[self.l[i][2]].y + 10) * FOV))
        end
    end

    --Calculating the forces.                            --#Forces#--
    ---@param f boolean
    function self.ls(f)
        --Init force variables.
        local length, distX, distY, distZ, damp, fracturePoint, deltaL
        --Calculate fracture, elasticity and damping.
        for i = 1, #self.l do
            --Calculate deformation.
            distX = self.v[self.l[i][2]].x - self.v[self.l[i][1]].x
            distY = self.v[self.l[i][2]].y - self.v[self.l[i][1]].y
            distZ = self.v[self.l[i][2]].z - self.v[self.l[i][1]].z
            length = math.sqrt(distX * distX + distY * distY + distZ * distZ)
            deltaL = length - self.l[i].l
            --Calculate fracture.
            if deltaL > self.l[i].f * self.l[i].l and f then
                --Make new vertices.
                self.v[#self.v + 1] = {
                    x = (self.v[self.l[i][2]].x + self.v[self.l[i][1]].x) / 2,
                    y = (self.v[self.l[i][2]].y + self.v[self.l[i][1]].y) / 2,
                    z = (self.v[self.l[i][2]].z + self.v[self.l[i][1]].z) / 2,
                    xv = (self.v[self.l[i][2]].xv + self.v[self.l[i][1]].xv) / 2,
                    yv = (self.v[self.l[i][2]].yv + self.v[self.l[i][1]].yv) / 2,
                    zv = (self.v[self.l[i][2]].zv + self.v[self.l[i][1]].zv) / 2,
                    m = (self.v[self.l[i][2]].m + self.v[self.l[i][1]].m) / 2,
                    fx = 0,
                    fy = 0,
                    fz = 0
                }
                self.v[#self.v + 1] = {
                    x = (self.v[self.l[i][2]].x + self.v[self.l[i][1]].x) / 2,
                    y = (self.v[self.l[i][2]].y + self.v[self.l[i][1]].y) / 2,
                    z = (self.v[self.l[i][2]].z + self.v[self.l[i][1]].z) / 2,
                    xv = (self.v[self.l[i][2]].xv + self.v[self.l[i][1]].xv) / 2,
                    yv = (self.v[self.l[i][2]].yv + self.v[self.l[i][1]].yv) / 2,
                    zv = (self.v[self.l[i][2]].zv + self.v[self.l[i][1]].zv) / 2,
                    m = (self.v[self.l[i][2]].m + self.v[self.l[i][1]].m) / 2,
                    fx = 0,
                    fy = 0,
                    fz = 0
                }
                --Make a new face.
                fracturePoint = self.l[i][2]
                self.l[i][2] = #self.v - 1
                self.l[i].f = self.l[i].f + self.l[i].f
                self.l[i].l = self.l[i].l / 2
                self.l[#self.l + 1] = {
                    #self.v,
                    fracturePoint,
                    l = self.l[i].l / 2,
                    k = self.l[i].k,
                    d = self.l[i].d,
                    f = self.l[i].f + self.l[i].f
                }
            end
            --Calculate damping.
            damp = (distX / length * (self.v[self.l[i][2]].xv - self.v[self.l[i][1]].xv) +
                distY / length * (self.v[self.l[i][2]].yv - self.v[self.l[i][1]].yv) +
                distZ / length * (self.v[self.l[i][2]].zv - self.v[self.l[i][1]].zv)) * self.l[i].d
            --Add all forces.
            self.v[self.l[i][1]].fx = self.v[self.l[i][1]].fx +
                (deltaL * self.l[i].k / self.l[i].l + damp) * distX / length
            self.v[self.l[i][1]].fy = self.v[self.l[i][1]].fy +
                (deltaL * self.l[i].k / self.l[i].l + damp) * distY / length
            self.v[self.l[i][1]].fz = self.v[self.l[i][1]].fz +
                (deltaL * self.l[i].k / self.l[i].l + damp) * distZ / length
            self.v[self.l[i][2]].fx = self.v[self.l[i][2]].fx -
                (deltaL * self.l[i].k / self.l[i].l + damp) * distX / length
            self.v[self.l[i][2]].fy = self.v[self.l[i][2]].fy -
                (deltaL * self.l[i].k / self.l[i].l + damp) * distY / length
            self.v[self.l[i][2]].fz = self.v[self.l[i][2]].fz -
                (deltaL * self.l[i].k / self.l[i].l + damp) * distZ / length
        end
        --Calculate other forces.
        for i = 1, #self.v do
            --Calculate gravity.
            self.v[i].fz = self.v[i].fz - 9.81
        end
    end

    --Simulating the mesh.                         --#Simulate#--
    ---@param f boolean
    ---@param deltaT number
    function self.s(f, deltaT)
        --Calculate all the forces.
        self.ls(f)
        --Prevent jumpscare.
        if deltaT > 0.1 then deltaT = 0 end
        for i = 1, #self.v do
            --Modify velocities accordingly.
            self.v[i].xv = self.v[i].xv + self.v[i].fx / self.v[i].m * deltaT
            self.v[i].yv = self.v[i].yv + self.v[i].fy / self.v[i].m * deltaT
            self.v[i].zv = self.v[i].zv + self.v[i].fz / self.v[i].m * deltaT
            --Reset forces
            self.v[i].fx = 0
            self.v[i].fy = 0
            self.v[i].fz = 0

            --Modify positions accordingly.

            self.v[i].x = self.v[i].x + self.v[i].xv * deltaT
            self.v[i].y = self.v[i].y + self.v[i].yv * deltaT
            self.v[i].z = self.v[i].z + self.v[i].zv * deltaT
            if self.v[i].z < 0 then
                self.v[i].xv = self.v[i].xv * 0.5 / 9.81 * self.v[i].m
                self.v[i].yv = self.v[i].yv * 0.5 / 9.81 * self.v[i].m
                self.v[i].z = -self.v[i].z
                self.v[i].zv = -self.v[i].zv * 0.5
            end
        end
    end
end

return new
-- local k1x, k1y = vx * dt, vy * dt
--   local k1vx, k1vy = f(x, y, vx, vy, m) / m * dt
--   local k2x, k2y = (vx + 0.5 * k1vx) * dt, (vy + 0.5 * k1vy) * dt
--   local k2vx, k2vy = f(x + 0.5 * k1x, y + 0.5 * k1y, vx + 0.5 * k1vx, vy + 0.5 * k1vy, m) / m * dt
--   local k3x, k3y = (vx + 0.5 * k2vx) * dt, (vy + 0.5 * k2vy) * dt
--   local k3vx, k3vy = f(x + 0.5 * k2x, y + 0.5 * k2y, vx + 0.5 * k2vx, vy + 0.5 * k2vy, m) / m * dt
--   local k4x, k4y = (vx + k3vx) * dt, (vy + k3vy) * dt
--   local k4vx, k4vy = f(x + k3x, y + k3y, vx + k3vx, vy + k3vy, m) / m * dt

--   -- Update position and velocity
--   local new_x = x + (k1x + 2 * k2x + 2 * k3x + k4x) / 6
--   local new_y = y + (k1y + 2 * k2y + 2 * k3y + k4y) / 6
--   local new_vx = vx + (k1vx + 2 * k2vx + 2 * k3vx + k4vx) / 6
--   local new_vy = vy + (k1vy + 2 * k2vy + 2 * k3vy + k4vy) / 6

--   return new_x, new_y, new_vx, new_vy

-- local k1x, k1y = self.v[i].vx * deltaT, self.v[i].vy * deltaT
--   local k1vx, k1vy = self.ls(self.v[i].x, self.v[i].y, self.v[i].vx, self.v[i].vy, self.v[i].m) / self.v[i].m * deltaT
--   local k2x, k2y = (self.v[i].vx + 0.5 * k1vx) * deltaT, (self.v[i].vy + 0.5 * k1vy) * deltaT
--   local k2vx, k2vy = self.ls(self.v[i].x + 0.5 * k1x, self.v[i].y + 0.5 * k1y, self.v[i].vx + 0.5 * k1vx, self.v[i].vy + 0.5 * k1vy, self.v[i].m) / self.v[i].m * deltaT
--   local k3x, k3y = (self.v[i].vx + 0.5 * k2vx) * deltaT, (self.v[i].vy + 0.5 * k2vy) * deltaT
--   local k3vx, k3vy = self.ls(self.v[i].x + 0.5 * k2x, self.v[i].y + 0.5 * k2y, self.v[i].vx + 0.5 * k2vx, self.v[i].vy + 0.5 * k2vy, self.v[i].m) / self.v[i].m * deltaT
--   local k4x, k4y = (self.v[i].vx + k3vx) * deltaT, (self.v[i].vy + k3vy) * deltaT
--   local k4vx, k4vy = self.ls(self.v[i].x + k3x, self.v[i].y + k3y, self.v[i].vx + k3vx, self.v[i].vy + k3vy, self.v[i].m) / self.v[i].m * deltaT

--   -- Update position and velocity
--   local newX = self.v[i].x + (k1x + 2 * k2x + 2 * k3x + k4x) / 6
--   local newY = self.v[i].y + (k1y + 2 * k2y + 2 * k3y + k4y) / 6
--   local newXV = self.v[i].vx + (k1vx + 2 * k2vx + 2 * k3vx + k4vx) / 6
--   local newYV = self.v[i].vy + (k1vy + 2 * k2vy + 2 * k3vy + k4vy) / 6

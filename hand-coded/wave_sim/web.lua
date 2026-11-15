---@param x_size integer
---@param y_size integer
---@param self table
local function newWeb(self, x_size, y_size)
    --The X size of the map.
    self.x = x_size
    --The Y size of the map.
    self.y = y_size
    --The map array.
    self.m = {}
    --If pause.
    self.r = false
    for x = 0, self.x + 1 do
        self.m[x] = {}
        for y = 0, self.y + 1 do
            self.m[x][y] = {}
            self.m[x][y].p = 0
            self.m[x][y].v = 0
            self.m[x][y].a = 0
        end
    end
    --The drawing function, where mode can only be: "v", "d", "t"
    function self.d(scale, mode, displayVelocity, displayAcceleration, probability)
        local r, g, b = 0, 0, 0
        if mode == "v" then
            for x = 1, self.x do
                for y = 1, self.y do
                    r = math.sig(self.m[x][y].p)
                    if displayVelocity then
                        g = math.sig(self.m[x][y].v)
                    else
                        g = math.sig(self.m[x][y].p)
                    end
                    if displayAcceleration then
                        b = math.sig(self.m[x][y].a)
                    else
                        b = math.sig(self.m[x][y].p)
                    end
                    love.graphics.setColor(r, g, b, 1)
                    love.graphics.rectangle("fill", (x - 1) * scale, (y - 1) * scale, scale, scale)
                end
            end
        end
        if mode == "d" then
            for x = 1, self.x do
                for y = 1, self.y do
                    r = math.sig(self.m[x][y].p - self.m[x][y - 1].p)
                    if displayVelocity then
                        g = math.sig(self.m[x][y].v - self.m[x][y - 1].v)
                    else
                        g = math.sig(self.m[x][y].p - self.m[x][y - 1].p)
                    end
                    if displayAcceleration then
                        b = math.sig(self.m[x][y].a - self.m[x][y - 1].a)
                    else
                        b = math.sig(self.m[x][y].p - self.m[x][y - 1].p)
                    end
                    love.graphics.setColor(r, g, b, 1)
                    love.graphics.rectangle("fill", (x - 1) * scale, (y - 1) * scale, scale, scale)
                end
            end
        end
        if mode == "t" then
            local r1, g1, b1, r2, g2, b2
            local zs, a = self.y / 2, self.y / 16
            local width, height = love.graphics.getWidth(), love.graphics.getHeight() / 16
            local pC = width / math.tan(math.rad(35))
            width = width / 2
            for x = 1, self.x do
                for y = 1, self.y do
                    if probability then
                        r = self.m[x][y].p ^ 2
                        r1 = self.m[x][y + 1].p ^ 2
                        r2 = self.m[x + 1][y].p ^ 2
                        if displayVelocity or displayAcceleration then
                            g = self.m[x][y].v ^ 2
                            g1 = self.m[x][y + 1].v ^ 2
                            g2 = self.m[x + 1][y].v ^ 2
                        end
                        if displayAcceleration then
                            b = self.m[x][y].a ^ 2
                            b1 = self.m[x][y + 1].a ^ 2
                            b2 = self.m[x + 1][y].a ^ 2
                        end
                    else
                        r = self.m[x][y].p
                        r1 = self.m[x][y + 1].p
                        r2 = self.m[x + 1][y].p
                        if displayVelocity or displayAcceleration then
                            g = self.m[x][y].v
                            g1 = self.m[x][y + 1].v
                            g2 = self.m[x + 1][y].v
                        end
                        if displayAcceleration then
                            b = self.m[x][y].a
                            b1 = self.m[x][y + 1].a
                            b2 = self.m[x + 1][y].a
                        end
                    end
                    if displayAcceleration then
                        love.graphics.setColor(0, 0, .5, .5)
                        love.graphics.line(
                            (x - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                            (-(b + g + r) * a + zs) / (-y + self.y * 2.5) * pC + height,
                            (x - self.x / 2) / ((-y - 1) + self.y * 2.5) * pC + width,
                            (-(b1 + g1 + r1) * a + zs) / ((-y - 1) + self.y * 2.5) * pC + height)
                        love.graphics.line(
                            (x - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                            (-(b + g + r) * a + zs) / (-y + self.y * 2.5) * pC + height,
                            ((x + 1) - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                            (-(b2 + g2 + r2) * a + zs) / (-y + self.y * 2.5) * pC + height)
                    end
                    if displayVelocity then
                        love.graphics.setColor(0, .75, 0, .5)
                        love.graphics.line(
                            (x - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                            (-(g + r) * a + zs) / (-y + self.y * 2.5) * pC + height,
                            (x - self.x / 2) / ((-y - 1) + self.y * 2.5) * pC + width,
                            (-(g1 + r1) * a + zs) / ((-y - 1) + self.y * 2.5) * pC + height)
                        love.graphics.line(
                            (x - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                            (-(g + r) * a + zs) / (-y + self.y * 2.5) * pC + height,
                            ((x + 1) - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                            (-(g2 + r2) * a + zs) / (-y + self.y * 2.5) * pC + height)
                    end
                    love.graphics.setColor(1, 0, 0, .5)
                    love.graphics.line(
                        (x - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                        (-r * a + zs) / (-y + self.y * 2.5) * pC + height,
                        (x - self.x / 2) / ((-y - 1) + self.y * 2.5) * pC + width,
                        (-r1 * a + zs) / ((-y - 1) + self.y * 2.5) * pC + height)
                    love.graphics.line(
                        (x - self.x / 2) / (-y + self.y * 2.5) * pC + width,
                        (-r * a + zs) / (-y + self.y * 2.5) * pC + height,
                        ((x + 1) - zs) / (-y + self.y * 2.5) * pC + width,
                        (-r2 * a + zs) / (-y + self.y * 2.5) * pC + height)
                end
            end
        end
        love.graphics.setColor(1, 1, 1, 1)
        love.graphics.print("Red = position", self.x * scale, 0)
        if displayVelocity then
            love.graphics.print("Green = velocity", self.x * scale, 15)
        else
            love.graphics.print("Green = position", self.x * scale, 15)
        end
        if displayAcceleration then
            love.graphics.print("Blue = acceleration", self.x * scale, 30)
        else
            love.graphics.print("Blue = position", self.x * scale, 30)
        end
        if mode == "v" then
            love.graphics.print("Mode: value (displays the sigmoid value of each pixel)", self.x * scale, 45)
        end
        if mode == "d" then
            love.graphics.print("Mode: derrivative (displays the sigmoid value of each pixels vretical derrivative)",
                self.x * scale, 45)
        end
        if mode == "t" then
            love.graphics.print("Mode: 3D (displays the exact value of each pixel through displacement)", self.x * scale,
                45)
        end
    end

    --The simulating function
    function self.s(d)
        if self.r then
            local cross, diagonal = 1 / (4 + math.sqrt(2) * 2), math.sqrt(2) / (8 + math.sqrt(2) * 4)
            for x = 1, self.x do
                for y = 1, self.y do
                    self.m[x][y].a = 0
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x - 1][y - 1].p) - self.m[x][y].p) * diagonal
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x - 1][y + 1].p) - self.m[x][y].p) * diagonal
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x + 1][y - 1].p) - self.m[x][y].p) * diagonal
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x + 1][y + 1].p) - self.m[x][y].p) * diagonal
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x - 1][y].p) - self.m[x][y].p) * cross
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x + 1][y].p) - self.m[x][y].p) * cross
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x][y - 1].p) - self.m[x][y].p) * cross
                    self.m[x][y].a = self.m[x][y].a + ((self.m[x][y + 1].p) - self.m[x][y].p) * cross
                end
            end
            for x = 1, self.x do
                for y = 1, self.y do
                    self.m[x][y].v = (self.m[x][y].v + self.m[x][y].a) * d
                    self.m[x][y].p = self.m[x][y].p + self.m[x][y].v
                end
            end
        end
    end
end

---@return table
---@nodiscard
return newWeb

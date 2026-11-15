--Create new object
local function new(self, vtx, faces)
    --The vertices of the object
    self.vtx = vtx
    --The faces of the object
    self.faces = faces
    --The edges of the object
    self.edge = {}
    for i = 1, #self.faces do
        self.edge[(i - 1) * 3 + 1] = { self.faces[i][1], self.faces[i][2] }
        self.edge[(i - 1) * 3 + 2] = { self.faces[i][2], self.faces[i][3] }
        self.edge[(i - 1) * 3 + 3] = { self.faces[i][3], self.faces[i][1] }
    end

    --The function that calculates the 2D-cross section of the 3D object
    function self.intersect()
        --Interpolation factor
        local t
        --The edge intersection index
        local j = 0
        --The edge intersections
        self.edge.i = {}
        --The face intersections
        self.faces.i = {}
        for i = 1, #self.edge do
            if self.vtx[self.edge[i][1]].y > 0 and self.vtx[self.edge[i][2]].y < 0 or
                self.vtx[self.edge[i][2]].y > 0 and self.vtx[self.edge[i][1]].y < 0 then
                j = j + 1
                self.edge.i[j] = {}
                if (self.vtx[self.edge[i][2]].y - self.vtx[self.edge[i][1]].y) == 0 then
                    self.edge.i[j].x = self.vtx[self.edge[i][1]].x
                    self.edge.i[j].y = self.vtx[self.edge[i][1]].z
                else
                    t = self.vtx[self.edge[i][1]].y / (self.vtx[self.edge[i][1]].y - self.vtx[self.edge[i][2]].y)
                    self.edge.i[j].x = self.vtx[self.edge[i][1]].x +
                        t * (self.vtx[self.edge[i][2]].x - self.vtx[self.edge[i][1]].x)
                    self.edge.i[j].y = self.vtx[self.edge[i][1]].z +
                        t * (self.vtx[self.edge[i][2]].z - self.vtx[self.edge[i][1]].z)
                end
                if j % 2 == 0 then
                    self.faces.i[j / 2] = { j, j - 1 }
                end
            end
        end
    end

    --Primarly run the intersection function
    self.intersect()

    --The edit functions:\
    --  -```self.edit.vtx()```;\
    --  -```self.edit.faces()```.
    self.edit = {}

    --The vertices editing function
    function self.edit.vtx(vtx)
        self.vtx = vtx
        self.intersect()
    end

    --The rotation editing function
    function self.edit.rot(xr, yr, zr)
        local x, y, z
        for i = 1, #self.vtx do
            y = self.vtx[i].y
            z = self.vtx[i].z
            self.vtx[i].y = y * math.cos(math.rad(xr)) + z * math.sin(math.rad(xr))
            self.vtx[i].z = -y * math.sin(math.rad(xr)) + z * math.cos(math.rad(xr))
            x = self.vtx[i].x
            z = self.vtx[i].z
            self.vtx[i].x = x * math.cos(math.rad(yr)) - z * math.sin(math.rad(yr))
            self.vtx[i].z = x * math.sin(math.rad(yr)) + z * math.cos(math.rad(yr))
            x = self.vtx[i].x
            y = self.vtx[i].y
            self.vtx[i].x = x * math.cos(math.rad(zr)) + y * math.sin(math.rad(zr))
            self.vtx[i].y = -x * math.sin(math.rad(zr)) + y * math.cos(math.rad(zr))
        end
        self.intersect()
    end

    --The scale editing function
    function self.edit.scale(xs, ys, zs)
        for i = 1, #self.vtx do
            self.vtx[i].x = self.vtx[i].x * xs
            self.vtx[i].y = self.vtx[i].y * ys
            self.vtx[i].z = self.vtx[i].z * zs
        end
        self.intersect()
    end

    --The move editing function
    function self.edit.move(xp, yp, zp)
        for i = 1, #self.vtx do
            self.vtx[i].x = self.vtx[i].x + xp
            self.vtx[i].y = self.vtx[i].y + yp
            self.vtx[i].z = self.vtx[i].z + zp
        end
        self.intersect()
    end

    --The vertices editing function
    function self.edit.faces(faces)
        self.faces = faces
        self.intersect()
    end

    --The function that draw the 2D-cross section of the 3D object
    function self.draw()
        --Interpolation factor
        local t
        --Screen dimension
        local height, width = love.graphics.getHeight(), love.graphics.getWidth()
        --Inside?
        local inside = false
        --Edge intersections
        local vtx = {}
        for y = 0, height do
            for i = 1, #self.faces.i do
                if self.edge.i[self.faces.i[i][2]].y == nil then
                    love.event.quit()
                end
                if self.edge.i[self.faces.i[i][1]].y + height / 2 > y and self.edge.i[self.faces.i[i][2]].y + height / 2 < y or
                    self.edge.i[self.faces.i[i][2]].y + height / 2 > y and self.edge.i[self.faces.i[i][1]].y + height / 2 < y then
                    if (self.edge.i[self.faces.i[i][2]].y - self.edge.i[self.faces.i[i][1]].y) == 0 then
                        vtx[i] = -self.edge.i[self.faces.i[i][1]].x
                    else
                        t = -self.edge.i[self.faces.i[i][1]].y /
                            (self.edge.i[self.faces.i[i][2]].y - self.edge.i[self.faces.i[i][1]].y)

                        vtx[i] = self.edge.i[self.faces.i[i][1]].x + t *
                            (self.edge.i[self.faces.i[i][2]].x - self.edge.i[self.faces.i[i][1]].x)
                    end
                end
            end
            for x = 0, width do
                for i = 1, #vtx do
                    if x > vtx[i] then
                        inside = not inside
                    end
                end
                if inside then
                    love.graphics.points(x * 100 + width / 2, y * 100 + height / 2)
                end
            end
        end
    end

    --Debugging function
    function self.debug()
        local height, width = love.graphics.getHeight(), love.graphics.getWidth()
        love.graphics.setColor(0, 1, 0, 1)
        for i = 1, #self.edge do
            love.graphics.line(
                self.vtx[self.edge[i][1]].x / (self.vtx[self.edge[i][1]].y + 10) * 1000 + width / 2,
                self.vtx[self.edge[i][1]].z / (self.vtx[self.edge[i][1]].y + 10) * 1000 + height / 2,
                self.vtx[self.edge[i][2]].x / (self.vtx[self.edge[i][2]].y + 10) * 1000 + width / 2,
                self.vtx[self.edge[i][2]].z / (self.vtx[self.edge[i][2]].y + 10) * 1000 + height / 2)
        end
        love.graphics.setColor(1, 0, 1, 1)
        for i = 1, #self.edge.i do
            if self.edge.i[i] ~= nil then
                love.graphics.points(self.edge.i[i].x * 100 + width / 2, self.edge.i[i].y * 10050 + height / 2)
            end
        end
        for i = 1, #self.faces.i do
            if self.faces.i[i] ~= nil then
                love.graphics.line(
                    self.edge.i[self.faces.i[i][1]].x * 100 + width / 2,
                    self.edge.i[self.faces.i[i][1]].y * 100 + height / 2,
                    self.edge.i[self.faces.i[i][2]].x * 100 + width / 2,
                    self.edge.i[self.faces.i[i][2]].y * 100 + height / 2)
            end
        end
        love.graphics.print(#self.vtx, 10, 0)
        love.graphics.print(#self.faces, 10, 15)
        love.graphics.print(#self.edge, 10, 30)
        love.graphics.print(#self.faces.i, 10, 45)
        love.graphics.print(#self.edge.i, 10, 60)
    end
end

return new

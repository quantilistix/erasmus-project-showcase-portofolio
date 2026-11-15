-- Command processor for LuaTracker
-- Implements a mini virtual machine with registers, stack, and commands

-- Use LuaJIT bit library for bitwise operations
local bit = require("bit")

Commands = {
    -- 256 registers (00-FF), shared across all channels
    registers = {},

    -- Call stack for call/return
    callStack = {},
    maxStackDepth = 64,

    -- Pause counter (extra wait time)
    pauseCounter = 0,

    -- Playback direction (1 = forward, -1 = backward)
    direction = 1,
    playBackwards = false,  -- Whether to play instruments backwards

    -- Debug mode
    debug = false,
}

-- Command constants
Commands.CMD = {
    NOP = 0x00,
    VOLUME = 0x01,
    VOLUME_INC = 0x02,
    VOLUME_DEC = 0x03,
    TEMPO = 0x04,
    TEMPO_INC = 0x05,
    TEMPO_DEC = 0x06,
    TEMPO_BACK = 0x07,
    TEMPO_BACK_PLAY = 0x08,
    PAUSE = 0x09,
    JUMP = 0x0A,
    CALL = 0x0B,
    RET = 0x0C,
    JCIZ = 0x0D,  -- Jump if 00 is zero
    JCNZ = 0x0E,  -- Jump if 00 is not zero
    JCIC = 0x0F,  -- Jump if FF is zero
    JCNC = 0x10,  -- Jump if FF is not zero
    LOAD_VAL = 0x11,
    LOAD_REG = 0x12,
    ADD_VAL = 0x13,
    ADD_REG = 0x14,
    SUB_VAL = 0x15,
    SUB_REG = 0x16,
    MUL_VAL = 0x17,
    MUL_REG = 0x18,
    DIV_VAL = 0x19,
    DIV_REG = 0x1A,
    MOD_VAL = 0x1B,
    MOD_REG = 0x1C,
    ROTL = 0x1D,
    ROTR = 0x1E,
    SHIFTLZ = 0x1F,
    SHIFTRZ = 0x20,
    SHIFTLO = 0x21,
    SHIFTRO = 0x22,
    NOP_FF = 0xFF,
}

-- Initialize command processor
function Commands:init()
    -- Initialize all 256 registers to 0
    self.registers = {}
    for i = 0, 255 do
        self.registers[i] = 0
    end

    -- Clear call stack
    self.callStack = {}

    -- Reset state
    self.pauseCounter = 0
    self.direction = 1
    self.playBackwards = false
end

-- Get register value
function Commands:getReg(index)
    return self.registers[index % 256] or 0
end

-- Set register value (clamps to 0-255)
function Commands:setReg(index, value)
    self.registers[index % 256] = value % 256
end

-- Set result register (00) and carry register (FF)
function Commands:setResult(value, carry)
    self.registers[0x00] = value % 256
    self.registers[0xFF] = carry and 1 or 0
end

-- Combine two operands into a 16-bit value
function Commands:combineOperands(op1, op2)
    return (op1 or 0) * 16 + (op2 or 0)
end

-- Push to call stack
function Commands:pushStack(orderIndex, row)
    if #self.callStack < self.maxStackDepth then
        table.insert(self.callStack, {orderIndex = orderIndex, row = row})
        return true
    end
    print("Warning: Call stack overflow")
    return false
end

-- Pop from call stack
function Commands:popStack()
    if #self.callStack > 0 then
        return table.remove(self.callStack)
    end
    print("Warning: Call stack underflow")
    return nil
end

-- Check if we should pause (extra wait)
function Commands:shouldPause()
    if self.pauseCounter > 0 then
        self.pauseCounter = self.pauseCounter - 1
        return true
    end
    return false
end

-- Execute a command
-- Returns: shouldJump, newOrderIndex, newRow
function Commands:execute(cmd, op1, op2, channel, tracker)
    if cmd == nil then
        return false, nil, nil
    end

    -- NOP commands - do nothing but still valid
    if cmd == self.CMD.NOP or cmd == self.CMD.NOP_FF then
        return false, nil, nil
    end

    op1 = op1 or 0
    op2 = op2 or 0

    if self.debug then
        print(string.format("CMD: ch=%d cmd=%02X op1=%02X op2=%02X", channel, cmd, op1, op2))
    end

    -- Volume commands (01-03) - channel specific
    if cmd == self.CMD.VOLUME then
        -- Set stereo volume: op1 = left, op2 = right (stored per channel)
        tracker.channelVolumes[channel] = {left = op1, right = op2}
        -- Also apply to currently playing source
        local source = tracker.channelSources[channel]
        if source then
            local vol = (op1 + op2) / 2 / 255
            source:setVolume(vol)
        end
        return false, nil, nil

    elseif cmd == self.CMD.VOLUME_INC then
        local vol = tracker.channelVolumes[channel] or {left = 255, right = 255}
        vol.left = math.min(255, vol.left + op1)
        vol.right = math.min(255, vol.right + op2)
        tracker.channelVolumes[channel] = vol
        -- Apply to currently playing source
        local source = tracker.channelSources[channel]
        if source then
            local avgVol = (vol.left + vol.right) / 2 / 255
            source:setVolume(avgVol)
        end
        return false, nil, nil

    elseif cmd == self.CMD.VOLUME_DEC then
        local vol = tracker.channelVolumes[channel] or {left = 255, right = 255}
        vol.left = math.max(0, vol.left - op1)
        vol.right = math.max(0, vol.right - op2)
        tracker.channelVolumes[channel] = vol
        -- Apply to currently playing source
        local source = tracker.channelSources[channel]
        if source then
            local avgVol = (vol.left + vol.right) / 2 / 255
            source:setVolume(avgVol)
        end
        return false, nil, nil

    -- Tempo commands (04-08)
    elseif cmd == self.CMD.TEMPO then
        tracker.tempo = self:combineOperands(op1, op2)
        if tracker.tempo < 1 then tracker.tempo = 1 end
        self.direction = 1
        self.playBackwards = false
        return false, nil, nil

    elseif cmd == self.CMD.TEMPO_INC then
        tracker.tempo = tracker.tempo + self:combineOperands(op1, op2)
        if tracker.tempo > 999 then tracker.tempo = 999 end
        return false, nil, nil

    elseif cmd == self.CMD.TEMPO_DEC then
        tracker.tempo = tracker.tempo - self:combineOperands(op1, op2)
        if tracker.tempo < 1 then tracker.tempo = 1 end
        return false, nil, nil

    elseif cmd == self.CMD.TEMPO_BACK then
        -- Backwards tempo, instruments play forwards
        tracker.tempo = self:combineOperands(op1, op2)
        if tracker.tempo < 1 then tracker.tempo = 1 end
        self.direction = -1
        self.playBackwards = false
        return false, nil, nil

    elseif cmd == self.CMD.TEMPO_BACK_PLAY then
        -- Backwards tempo, instruments play backwards
        tracker.tempo = self:combineOperands(op1, op2)
        if tracker.tempo < 1 then tracker.tempo = 1 end
        self.direction = -1
        self.playBackwards = true
        return false, nil, nil

    -- Pause (09)
    elseif cmd == self.CMD.PAUSE then
        self.pauseCounter = self:combineOperands(op1, op2)
        return false, nil, nil

    -- Jump commands (0A-10)
    elseif cmd == self.CMD.JUMP then
        -- Jump to pattern (op1), row (op2)
        return true, op1 + 1, op2  -- +1 because playOrder is 1-indexed

    elseif cmd == self.CMD.CALL then
        -- Save return address (row after the call)
        self:pushStack(tracker.currentOrderIndex, tracker.currentRow + 1)
        return true, op1 + 1, op2

    elseif cmd == self.CMD.RET then
        -- Return from call
        local addr = self:popStack()
        if addr then
            return true, addr.orderIndex, addr.row
        end
        return false, nil, nil

    elseif cmd == self.CMD.JCIZ then
        -- Jump if register 00 is zero
        if self:getReg(0x00) == 0 then
            return true, op1 + 1, op2
        end
        return false, nil, nil

    elseif cmd == self.CMD.JCNZ then
        -- Jump if register 00 is not zero
        if self:getReg(0x00) ~= 0 then
            return true, op1 + 1, op2
        end
        return false, nil, nil

    elseif cmd == self.CMD.JCIC then
        -- Jump if register FF is zero
        if self:getReg(0xFF) == 0 then
            return true, op1 + 1, op2
        end
        return false, nil, nil

    elseif cmd == self.CMD.JCNC then
        -- Jump if register FF is not zero
        if self:getReg(0xFF) ~= 0 then
            return true, op1 + 1, op2
        end
        return false, nil, nil

    -- Register operations (11-22)
    elseif cmd == self.CMD.LOAD_VAL then
        -- Load value into register: reg[op1] = op2
        self:setReg(op1, op2)
        return false, nil, nil

    elseif cmd == self.CMD.LOAD_REG then
        -- Load register into register: reg[op1] = reg[op2]
        self:setReg(op1, self:getReg(op2))
        return false, nil, nil

    elseif cmd == self.CMD.ADD_VAL then
        -- Add value to register: 00 = reg[op1] + op2, FF = carry
        local result = self:getReg(op1) + op2
        self:setResult(result, result > 255)
        return false, nil, nil

    elseif cmd == self.CMD.ADD_REG then
        -- Add registers: 00 = reg[op1] + reg[op2], FF = carry
        local result = self:getReg(op1) + self:getReg(op2)
        self:setResult(result, result > 255)
        return false, nil, nil

    elseif cmd == self.CMD.SUB_VAL then
        -- Subtract value from register: 00 = reg[op1] - op2, FF = borrow
        local result = self:getReg(op1) - op2
        self:setResult(result < 0 and (256 + result) or result, result < 0)
        return false, nil, nil

    elseif cmd == self.CMD.SUB_REG then
        -- Subtract registers: 00 = reg[op1] - reg[op2], FF = borrow
        local result = self:getReg(op1) - self:getReg(op2)
        self:setResult(result < 0 and (256 + result) or result, result < 0)
        return false, nil, nil

    elseif cmd == self.CMD.MUL_VAL then
        -- Multiply: 00 = (reg[op1] * op2) % 256, FF = overflow
        local result = self:getReg(op1) * op2
        self:setResult(result, result > 255)
        return false, nil, nil

    elseif cmd == self.CMD.MUL_REG then
        -- Multiply registers: 00 = (reg[op1] * reg[op2]) % 256, FF = overflow
        local result = self:getReg(op1) * self:getReg(op2)
        self:setResult(result, result > 255)
        return false, nil, nil

    elseif cmd == self.CMD.DIV_VAL then
        -- Divide: 00 = reg[op1] / op2, FF = remainder exists
        if op2 == 0 then
            self:setResult(255, true)  -- Division by zero
        else
            local result = math.floor(self:getReg(op1) / op2)
            self:setResult(result, self:getReg(op1) % op2 ~= 0)
        end
        return false, nil, nil

    elseif cmd == self.CMD.DIV_REG then
        -- Divide registers: 00 = reg[op1] / reg[op2], FF = remainder exists
        local divisor = self:getReg(op2)
        if divisor == 0 then
            self:setResult(255, true)
        else
            local result = math.floor(self:getReg(op1) / divisor)
            self:setResult(result, self:getReg(op1) % divisor ~= 0)
        end
        return false, nil, nil

    elseif cmd == self.CMD.MOD_VAL then
        -- Modulo: 00 = reg[op1] % op2
        if op2 == 0 then
            self:setResult(0, true)
        else
            self:setResult(self:getReg(op1) % op2, false)
        end
        return false, nil, nil

    elseif cmd == self.CMD.MOD_REG then
        -- Modulo registers: 00 = reg[op1] % reg[op2]
        local divisor = self:getReg(op2)
        if divisor == 0 then
            self:setResult(0, true)
        else
            self:setResult(self:getReg(op1) % divisor, false)
        end
        return false, nil, nil

    -- Bit operations (1D-22) using LuaJIT bit library
    elseif cmd == self.CMD.ROTL then
        -- Rotate left: reg[op1] = rotate_left(reg[op1], op2)
        local val = self:getReg(op1)
        local amount = op2 % 8
        local result = bit.band(bit.bor(bit.lshift(val, amount), bit.rshift(val, 8 - amount)), 0xFF)
        self:setReg(op1, result)
        return false, nil, nil

    elseif cmd == self.CMD.ROTR then
        -- Rotate right: reg[op1] = rotate_right(reg[op1], op2)
        local val = self:getReg(op1)
        local amount = op2 % 8
        local result = bit.band(bit.bor(bit.rshift(val, amount), bit.lshift(val, 8 - amount)), 0xFF)
        self:setReg(op1, result)
        return false, nil, nil

    elseif cmd == self.CMD.SHIFTLZ then
        -- Shift left, fill with 0: reg[op1] = reg[op1] << op2
        local val = self:getReg(op1)
        local result = bit.band(bit.lshift(val, op2), 0xFF)
        self:setReg(op1, result)
        return false, nil, nil

    elseif cmd == self.CMD.SHIFTRZ then
        -- Shift right, fill with 0: reg[op1] = reg[op1] >> op2
        local val = self:getReg(op1)
        local result = bit.rshift(val, op2)
        self:setReg(op1, result)
        return false, nil, nil

    elseif cmd == self.CMD.SHIFTLO then
        -- Shift left, fill with 1
        local val = self:getReg(op1)
        local mask = bit.lshift(1, op2) - 1  -- Create mask of op2 ones
        local result = bit.band(bit.bor(bit.lshift(val, op2), mask), 0xFF)
        self:setReg(op1, result)
        return false, nil, nil

    elseif cmd == self.CMD.SHIFTRO then
        -- Shift right, fill with 1
        local val = self:getReg(op1)
        local mask = bit.lshift(bit.lshift(1, op2) - 1, 8 - op2)  -- Ones in high bits
        local result = bit.band(bit.bor(bit.rshift(val, op2), mask), 0xFF)
        self:setReg(op1, result)
        return false, nil, nil
    end

    return false, nil, nil
end

return Commands

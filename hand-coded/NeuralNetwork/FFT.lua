--This is the Discrete Fourier Transform (DFT).
-- local function DFT(signal)
--     --Number of smaples in the signal.
--     local n = #signal

--     --If the length of the signal is 1 the the answer is the signal itself.
--     if n <= 1 then
--         return signal
--     end

--     --The results of the DFT.
--     local results = {}

--     --The amplitudes of the signals.
--     results.amplitude = {}

--     --The phases of the signals.
--     results.dephase = {}

--     --The area under the graph.
--     local sum = 0

--     --The cosine area.
--     local cSum = 0

--     --The sine area.
--     local sSum = 0

--     --The total ratio of the sine and cosine wave.
--     local ratio = 0

--     --Get the frequencies.
--     for f = 0, n do
--         sum = 0
--         ratio = 0
--         for i = 1, n do
--             sSum = sSum + signal[i] * math.sin(i * f)
--             cSum = cSum + signal[i] * math.cos(i * f)
--         end
--         sum = cSum + sSum
--         ratio = cSum / sSum

--         results.amplitude[f + 1] = sum / n
--         results.dephase[f + 1] = ratio / n
--     end

--     return results
-- end

-- return DFT

--This is the fast fourier transform.
function FFT(x)
    --Signal length.
    local n = #x

    if n <= 1 then
        return x
    end

    --The even part of the signal.
    local even = {}

    --The odd part of the signal.
    local odd = {}

    for i = 1, n, 2 do
        even[(i - 1) / 2] = x[i]
        odd[(i - 1) / 2] = x[i + 1] or 0
    end

    --Recursively calculating the fft of the even and odd parts.
    even = FFT(even)
    odd = FFT(odd)

    --This is the final sum.
    local sum = 0

    --This is the final difference.
    local diff = 0

    --This is the final result.
    local result = {}

    --The butterfly operation
    for j = 0, n / 2 - 1 do
        --The angle of the sine/cosine wave
        local angle = -2 * math.pi * j / n
        result[j + 1] = even[j] + odd[j] * math.cos(angle)
        result[j + 1 + n / 2] = even[j] - odd[j] * math.sin(angle)
    end

    return result
end

return FFT

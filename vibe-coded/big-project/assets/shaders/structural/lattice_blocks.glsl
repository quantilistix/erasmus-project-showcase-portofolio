// 6×6 blocks of the lattice's stiffness for building the multigrid hierarchy, row-major
// (StressLattice.InterfaceBlock, FineDiagonal, FineCoupling, Galerkin and Invert).

// B_L or B_U: the gap across a face is B_L·x_L + B_U·x_U, B = [±I, h/2·[e]×], 3×6 row-major.
void gapMatrix(int axis, bool upper, out float m[18])
{
    float halfCell = 0.5 * cellSize();
    for (int k = 0; k < 18; k++)
        m[k] = 0.0;
    for (int s = 0; s < 3; s++)
        m[s * 6 + s] = upper ? 1.0 : -1.0;

    int a1 = (axis + 1) % 3;
    int a2 = (axis + 2) % 3;
    m[a2 * 6 + 3 + a1] = halfCell;
    m[a1 * 6 + 3 + a2] = -halfCell;
}

// One block of an interface's stiffness: K_ab = B_aᵀ·Kt·B_b + C_aᵀ·Kr·C_b.
void interfaceBlock(int axis, vec4 k, bool rowUpper, bool columnUpper, out float block[BLOCK])
{
    float gapRow[18];
    float gapColumn[18];
    gapMatrix(axis, rowUpper, gapRow);
    gapMatrix(axis, columnUpper, gapColumn);
    float twistSign = rowUpper == columnUpper ? 1.0 : -1.0;

    for (int r = 0; r < 6; r++)
    {
        for (int c = 0; c < 6; c++)
        {
            float sum = 0.0;
            for (int s = 0; s < 3; s++)
                sum += gapRow[s * 6 + r] * (s == axis ? k.x : k.y) * gapColumn[s * 6 + c];

            if (r >= 3 && r == c)
                sum += twistSign * (r - 3 == axis ? k.w : k.z);

            block[r * 6 + c] = sum;
        }
    }
}

// Level 0's diagonal block of a cell: its interfaces' own blocks plus its supports.
void fineDiagonal(int i, ivec3 c, ivec3 size, out float block[BLOCK])
{
    float own[BLOCK];
    for (int k = 0; k < BLOCK; k++)
        block[k] = 0.0;

    float s = anchorStiffness(i);
    for (int k = 0; k < 3; k++)
    {
        block[k * 7] = s;
        block[(k + 3) * 7] = s * cellSize() * cellSize();
    }

    for (int d = 0; d < 6; d++)
    {
        ivec3 n = c + DIRECTIONS[d];
        if (!inside(n, size))
            continue;

        int j = nodeIndex(n, size);
        if (cells[j].w == 0.0)
            continue;

        bool lower = (d & 1) == 0;
        interfaceBlock(d >> 1, bondBetween(i, j, d), !lower, !lower, own);
        for (int k = 0; k < BLOCK; k++)
            block[k] += own[k];
    }
}

// Level 0's coupling block from cell i to its occupied neighbour j in direction d.
void fineCoupling(int i, int j, int d, out float block[BLOCK])
{
    bool lower = (d & 1) == 0;
    interfaceBlock(d >> 1, bondBetween(i, j, d), !lower, lower, block);
}

// P(r) = [I, −[r]×; 0, I]: an aggregate's rigid motion seen at offset r.
void prolongator(vec3 r, out double m[BLOCK])
{
    for (int k = 0; k < BLOCK; k++)
        m[k] = 0.0LF;
    for (int k = 0; k < 6; k++)
        m[k * 7] = 1.0LF;

    m[0 * 6 + 4] = r.z;
    m[0 * 6 + 5] = -r.y;
    m[1 * 6 + 3] = -r.z;
    m[1 * 6 + 5] = r.x;
    m[2 * 6 + 3] = r.y;
    m[2 * 6 + 4] = -r.x;
}

// sum += P(ri)ᵀ·K·P(rj), in double.
void addGalerkin(inout double sum[BLOCK], vec3 ri, double k[BLOCK], vec3 rj)
{
    double pi[BLOCK];
    double pj[BLOCK];
    double kp[BLOCK];
    prolongator(ri, pi);
    prolongator(rj, pj);

    for (int r = 0; r < 6; r++)
    {
        for (int c = 0; c < 6; c++)
        {
            double total = 0.0LF;
            for (int s = 0; s < 6; s++)
                total += k[r * 6 + s] * pj[s * 6 + c];
            kp[r * 6 + c] = total;
        }
    }

    for (int r = 0; r < 6; r++)
    {
        for (int c = 0; c < 6; c++)
        {
            double total = 0.0LF;
            for (int s = 0; s < 6; s++)
                total += pi[s * 6 + r] * kp[s * 6 + c];
            sum[r * 6 + c] += total;
        }
    }
}

// Inverts a symmetric positive definite block through its Cholesky factor. A pivot rounding
// made non-positive becomes a tiny positive one.
void invertBlock(double a[BLOCK], out double inverse[BLOCK])
{
    double largest = 0.0LF;
    for (int i = 0; i < 6; i++)
        largest = max(largest, abs(a[i * 7]));
    double floorValue = max(largest * 1e-14LF, 1e-300LF);

    for (int j = 0; j < 6; j++)
    {
        double pivot = a[j * 6 + j];
        for (int k = 0; k < j; k++)
            pivot -= a[j * 6 + k] * a[j * 6 + k];
        pivot = sqrt(max(pivot, floorValue));
        a[j * 6 + j] = pivot;

        for (int i = j + 1; i < 6; i++)
        {
            double sum = a[i * 6 + j];
            for (int k = 0; k < j; k++)
                sum -= a[i * 6 + k] * a[j * 6 + k];
            a[i * 6 + j] = sum / pivot;
        }
    }

    for (int c = 0; c < 6; c++)
    {
        double column[6] = double[6](0.0LF, 0.0LF, 0.0LF, 0.0LF, 0.0LF, 0.0LF);
        column[c] = 1.0LF;
        for (int i = 0; i < 6; i++)
        {
            double sum = column[i];
            for (int k = 0; k < i; k++)
                sum -= a[i * 6 + k] * column[k];
            column[i] = sum / a[i * 6 + i];
        }

        for (int i = 5; i >= 0; i--)
        {
            double sum = column[i];
            for (int k = i + 1; k < 6; k++)
                sum -= a[k * 6 + i] * column[k];
            column[i] = sum / a[i * 6 + i];
        }

        for (int r = 0; r < 6; r++)
            inverse[r * 6 + c] = column[r];
    }
}

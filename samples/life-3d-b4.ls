// 3D Life, B4/S: a dead cell with exactly 4 live neighbors comes alive, and every live cell
// dies the next generation. Uses the 24-cell neighborhood from Conway3D (the 3x3x3 cube
// without the cells straight above and below).
//
// In B/S notation, counts that aren't listed mean dead. In lifescript a cell that writes
// nothing keeps its state, so the dying has to be written out. `born [4] survive []` is the
// short form of the if below.
Properties:
    Title = "3D Life (B4/S)"
    Dimensions = (32, 32, 32)
    Wraparound = true
    Seed = 2
    Start = random 0.3 of "cell" in (8, 8, 8) to (23, 23, 23)

type cell:
    Neighborhood[3, 3] = { 1, 1, 1,
                           1, 0, 1,
                           1, 1, 1 ;
                           1,   1, 1,
                           1, VAR, 1,
                           1,   1, 1 ;
                           1, 1, 1,
                           1, 0, 1,
                           1, 1, 1 }
    if dead then
        if neighbors == 4 then alive endif     // otherwise stays dead
    else
        dead
    endif
endtype

// Conway3D from CellularAutomata.NET: Life on a 3x3x3 block minus the cells directly above
// and below (24 neighbors), drawn slice by slice. Slices are separated by ';'.
Properties:
    Title = "Conway 3D (extruded Moore)"
    Dimensions = (32, 32, 32)
    Wraparound = true
    Seed = 2
    Start = random 1 of "cell" in (4, 4, 4) to (28, 28, 28)

type cell:
    statecolor 1 is #5f9e6e
    Neighborhood = moore(1)
    if alive then
        if neighbors < 5 | neighbors > 7 then dead endif
    else
        if neighbors == 6 then alive endif
    endif
endtype

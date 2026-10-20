// Conway3D from CellularAutomata.NET: Life on a 3x3x3 block minus the cells directly above
// and below (24 neighbors), drawn slice by slice. Slices are separated by ';'.
Properties:
	Title = "Conway 3D (extruded Moore)"
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
	if alive then
		if neighbors < 5 | neighbors > 7 then dead endif
	else
		if neighbors == 6 then alive endif
	endif
endtype

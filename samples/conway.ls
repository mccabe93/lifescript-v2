// Conway's Game of Life, with a Gosper glider gun from an RLE pattern.
Properties:
	Title = "Conway's Game of Life"
	Dimensions = (128, 96)
	Wraparound = true
	Start = pattern "24bo$22bobo$12b2o6b2o12b2o$11bo3bo4b2o12b2o$2o8bo5bo3b2o$2o8bo3bob2o4bobo$10bo5bo7bo$11bo3bo$12b2o!" of "life" at (4, 4)

type life:
	Neighborhood[3] = { 1,   1, 1,
	                    1, VAR, 1,
	                    1,   1, 1 }
	statecolor 1 is #f2f2e8

	if alive then
		if neighbors < 2 | neighbors > 3 then dead endif
	else
		if neighbors == 3 then alive endif
	endif
endtype

// Larger than Life: Kellie Evans' "Bugs" (Bosco's rule), radius 5.
// 120 neighbors, still compiles to a 242-entry lookup table.
Properties:
	Title = "Bugs (Larger than Life)"
	Dimensions = (160, 120)
	Wraparound = true
	Seed = 11
	Start = random 0.5 of "cell" in (50, 30) to (109, 89)

type cell:
	Neighborhood = moore(5)
	statecolor 1 is #ffb15c
	n = neighbors
	if alive then
		if n < 34 | n > 58 then dead endif
	else
		if n in [34..45] then alive endif
	endif
endtype

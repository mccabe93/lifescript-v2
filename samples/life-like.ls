// Any Life-like rule, written with `in` lists. Swap the lists to try others:
//   HighLife  B36/S23      Day & Night  B3678/S34678      Seeds  B2/S
Properties:
	Title = "Day & Night"
	Dimensions = (160, 120)
	Wraparound = true
	Seed = 7
	Start = random 0.5 of "cell" in (40, 30) to (119, 89)

type cell:
	Neighborhood = moore(1)
	if alive then
		if neighbors in [3, 4, 6..8] then alive else dead endif
	else
		if neighbors in [3, 6..8] then alive endif
	endif
endtype

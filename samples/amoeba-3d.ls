// 3D Generations rule "Amoeba" (S9-26 / B5-7,12-13,15 / 5 states) on the 26-cell Moore
// neighborhood. Only state 1 counts as a live neighbor; states 2-4 are dying.
Properties:
	Title = "Amoeba (3D)"
	Dimensions = (48, 48, 48)
	Wraparound = true
	Seed = 5
	Start = random 0.5 of "cell" in (19, 19, 19) to (28, 28, 28)

type cell:
	States = 5
	Neighborhood = moore(1)
	statecolor 1 is #ffc46b
	statecolor 2 is #ff8a4c
	statecolor 3 is #d9668f
	statecolor 4 is #6b3f8f
	n = neighbors with state 1
	if state == 0 then
		if n in [5..7, 12, 13, 15] then setstate to 1 endif
	elseif state == 1 then
		if !(n in [9..26]) then setstate to 2 endif
	else
		setstate to (state + 1) % 5
	endif
endtype

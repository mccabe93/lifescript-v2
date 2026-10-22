// Wireworld: electrons run along copper. Types replace the original's state juggling.
Properties:
	Title = "Wireworld"
	Dimensions = (40, 21)
	Default Type = "empty"
	// A loop with cut corners is a clock: one electron circles forever.
	Start = pattern "b10o$o10bo$b10o!" of "copper" at (2, 2),
	        ("head", (5, 2)), ("tail", (4, 2)),
	        pattern "34o!" of "copper" at (2, 12),
	        ("head", (4, 12)), ("tail", (3, 12))

type empty:
	Neighborhood = none
	setcolor to #15101b
endtype

type copper:
	Neighborhood = moore(1)
	setcolor to #ffb15c
	heads = neighbors that are "head"
	if heads == 1 | heads == 2 then become "head" endif
endtype

type head:
	Neighborhood = none
	setcolor to #7fd3e0
	become "tail"
endtype

type tail:
	Neighborhood = none
	setcolor to #d9668f
	become "copper"
endtype

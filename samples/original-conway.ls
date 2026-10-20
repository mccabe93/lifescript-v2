Properties:
	Title = "Conway's Game of Life"
	Cell Size = (10, 10)
	Dimensions = (60, 60)
	Wraparound = true
	// The glider gun shown on the Wikipedia page
	Start = {("block", (2,9)),("block", (3,9)),("block", (2,10)),("block", (3,10)),							
										("block", (26,5)),
										("block", (24,6)),("block", (26,6)),
("block", (14,7)),("block", (15,7)),						("block", (22,7)),("block", (23,7)),
("block", (13,8)),("block", (17,8)),						("block", (22,8)),("block", (23,8)),
("block", (12,9)),("block", (18,9)),						("block", (22,9)),("block", (23,9)),
("block", (12,10)),("block", (16,10)),("block", (18,10)),("block", (19,10)),	("block", (24,10)),("block", (26,10)),
("block", (12,11)),("block", (18,11)),						("block", (26,11)),
("block", (13,12)),("block", (17,12)),
("block", (14,13)),("block", (15,13)),

("block", (36,7)),("block", (37,7)),("block", (36,8)),("block", (37,8))
}
	Default Type = "block"
	Generations = 1024
	Interval = 0.10
	Pausable = true

type block:
	Neighborhood[3] = {	1,	1, 	1,
				1,	VAR,	1,
				1,	1, 	1	}

	if alive then
		if((neighbors == 2) | (neighbors == 3)) then
			alive
		else
			dead
		endif
	else
		if neighbors == 3 then
			alive
		endif
	endif
endtype

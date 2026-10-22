// Forest fire (Drossel-Schwabl): trees grow, lightning strikes, fire spreads.
// Uses chance(), so every cell is evaluated every generation and there's no lookup table.
growth = 0.01
lightning = 0.00002

Properties:
	Title = "Forest fire"
	Dimensions = (200, 150)
	Wraparound = true
	Seed = 3
	Default Type = "ground"
	Start = random 0.55 of "tree"

type ground:
	Neighborhood = none
	setcolor to #1d1418
	if chance(growth) then become "tree" endif
endtype

type tree:
	Neighborhood = vonneumann(1)
	setcolor to #5f9e6e
	if neighbors that are "fire" > 0 | chance(lightning) then become "fire" endif
endtype

type fire:
	Neighborhood = none
	setcolor to #ff6f3c
	become "ground"
endtype

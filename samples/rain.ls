// The world: block runs once per generation before the cells do. Here it drops rain at
// random spots along the top; cells only ever look at the cell above them.
drops = 3

Properties:
	Title = "Rain"
	Dimensions = (120, 80)
	Default Type = "air"

world:
	for i = 1 to drops
		create "drop" at (random(0, world.width - 1), 0)
	next i
	total = count "drop"
	if generation % 50 == 0 then print "generation ", generation, ": ", total, " drops" endif
endworld

type air:
	setcolor to #0c1222
	Neighborhood = none
	if neighbor at (0, -1) is "drop" then become "drop" endif
endtype

type drop:
	setcolor to #7cc4ff
	Neighborhood = none
	become "air"
endtype

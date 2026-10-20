// Wolfram's elementary rules in 2D: each row reads the row above it, so the grid fills in
// as a space-time diagram. The weights 4, 2, 1 turn the three cells above into a number
// from 0 to 7, and bit() looks that number up in the rule.
rule = 30

Properties:
	Title = "Rule 30"
	Dimensions = (201, 100)
	Start = ("cell", (100, 0))

type cell:
	Neighborhood[3] = { 4,   2, 1,
	                    0, VAR, 0 }
	// The top row has nothing above it, so it keeps its seed. (This also means pattern 000
	// never fires, so odd-numbered rules don't work yet; see docs/NOTES.md.)
	if neighbors == 0 then return endif
	setstate to bit(rule, neighbors)
endtype

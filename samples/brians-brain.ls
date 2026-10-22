// Brian's Brain: firing cells always become refractory, then ready again.
Properties:
	Title = "Brian's Brain"
	Dimensions = (160, 120)
	Wraparound = true
	Start = random 0.25 of "neuron" in (60, 40) to (99, 79)

type neuron:
	States = 3
	Neighborhood = moore(1)
	statecolor 0 is #15101b
	statecolor 1 is #ffe3c2
	statecolor 2 is #d9668f

	if state == 0 & neighbors with state 1 == 2 then setstate to 1 endif
	if state == 1 then setstate to 2 endif
	if state == 2 then setstate to 0 endif
endtype

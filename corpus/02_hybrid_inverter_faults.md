# Common hybrid inverter faults and what they mean

A hybrid inverter that repeatedly switches to bypass mode under load is usually reporting an overload or an over-temperature condition rather than a grid problem. Check the event log for codes in the overload family first. A 5 kVA unit will typically carry 100 percent load continuously, 110 percent for 10 minutes, and 150 percent for 10 seconds before transferring load to the grid or shutting down.

Fault code F07 on many Growatt and Deye hybrid units indicates low battery voltage. The inverter has reached its low-voltage cutoff and has stopped discharging to protect the battery. On a 48 V lithium iron phosphate bank the cutoff is normally set between 46.0 and 47.0 V; on lead-acid it should be no lower than 44.0 V to avoid damaging the cells.

Fault code F08 commonly indicates battery over-voltage. This happens when the charge voltage is set higher than the battery management system allows, or when a lithium bank's BMS disconnects at the top of charge and the inverter sees the open-circuit voltage jump. The fix is to lower the bulk and float charge settings to match the battery manufacturer's figures, not to reset the fault repeatedly.

An inverter that shows solar input but will not charge the battery often has the PV input working mode set to grid priority or load priority instead of battery priority. On units with a time-of-use scheduler, a charging window that excludes daylight hours produces the same symptom. Confirm the working mode before suspecting the charge controller.

A persistent ground fault or isolation alarm points to moisture in a PV combiner box, damaged insulation on the DC cable run, or a cracked panel. It is not safe to clear this alarm and continue operating; locate the fault with an insulation resistance tester at 500 V DC, string by string.

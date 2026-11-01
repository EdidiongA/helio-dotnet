# Lithium iron phosphate battery charge parameters

A 48 V lithium iron phosphate bank is built from 16 cells in series, abbreviated 16S. Each cell has a nominal voltage of 3.2 V, giving a nominal pack voltage of 51.2 V. The recommended bulk or absorption charge voltage for a 16S bank is 55.2 to 56.0 V, which is 3.45 to 3.50 V per cell. Float should be set at 53.6 to 54.0 V, or disabled entirely, because lithium cells do not need a float stage.

Charging above 56.8 V, which is 3.55 V per cell, triggers most battery management systems to disconnect for over-voltage protection. Repeated high-voltage charging shortens cycle life even if the BMS does not trip. Charging below 0 degrees Celsius causes lithium plating and permanent capacity loss; quality batteries refuse charge below this temperature through the BMS.

Depth of discharge drives cycle life more than any other setting. A lithium iron phosphate cell rated at 6,000 cycles at 80 percent depth of discharge will typically deliver 3,500 cycles at 100 percent and more than 8,000 at 50 percent. For a household bank, set the inverter low-voltage cutoff so that about 10 to 20 percent remains, which on a 16S bank corresponds to a cutoff of about 48.0 to 49.0 V under light load.

State of charge cannot be estimated accurately from voltage on lithium iron phosphate because the discharge curve is flat: the pack sits between 52.0 and 53.2 V from roughly 20 percent to 90 percent state of charge. Use the battery management system's coulomb-counting state of charge, communicated to the inverter over CAN or RS485, rather than a voltage table.

Lead-acid banks have different numbers. A 48 V flooded bank absorbs at 57.6 to 58.4 V, floats at 54.0 to 55.2 V, and should not be discharged below 50 percent for daily cycling if it is to reach its rated life.

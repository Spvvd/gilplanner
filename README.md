# Gil Planner (Dalamud plugin)

Track craftable items and compare three ways of making gil, using live Universalis market data:

| Column | Meaning |
| --- | --- |
| My world | The sell price on your current world (see Robust sell price below); hover for lowest listing and average sale |
| Cheapest (DC) | Cheapest listing in your data center, and on which world |
| Flip profit | Buy at the cheapest DC listing, sell on your world |
| Craft profit | Buy all materials at the cheapest DC prices (vendor price if lower, sub-recipes crafted if cheaper), craft, sell on your world |
| Gather profit | As craft, but gatherable materials (mining, botany, fishing, crystals) cost 0 gil |
| Sales/day | Sales per day on your world (Universalis sale history) |
| Profit/day | Best strategy's profit per item x sales/day x your expected share of the sales (Settings, 25% by default). Other sellers compete for the same buyers, so it is an estimate, not a promise. Hover for the formula and how long a batch takes to sell |
| Data age | How long ago players last uploaded your world's market board for this item. Over 6 hours turns amber, over 2 days red (the price may be a ghost listing). The same check flags the cheapest DC world in the "Cheapest (DC)" column |

Profits are per item, in gil and as a percentage of cost, after market tax (5%, adjustable).

- **Quantity-aware material costs.** Materials are costed for a batch (10 items by default, see Settings): the
  cheapest listings in your DC are bought one after another, so large quantities cost more per unit. Hover a craft or
  gather cell for the breakdown, click it for a list where you can click a craftable material to open it in the
  crafting log. It shows which world to buy on and warns when the listings run out.
- **Robust sell price.** By default the sell price is the median of the cheapest 5 listings on your world, so one
  lowball does not set the price. A yellow `!` appears when the price is more than 30% away from the recent average
  sale price. You can switch to the plain lowest listing or the average sale price in Settings.
- **Row actions.** Right-click an item to show a clickable item link in chat (only you see it), open it on
  Universalis, or open it in the crafting log. Right-click a row in the Market tab to remove it.
- Adding several items in a row makes one refresh shortly after the last one, not one per item.
- Add items by right-clicking a craftable item in game ("Add to Gil Planner"), with the search box, or from the
  Popular tab. `/gilplanner` toggles the window.

The **Popular** tab lists craftable items that sell the most on your world (units/day, estimated gil/day). Right-click
a row or use the "Add" button to move one into your list. It scans all ~9,500 craftable marketable items on
Universalis (about 75 seconds). Scans are saved to disk and reused for 12 hours (adjustable), so after a restart you
see the last result at once. A new scan starts automatically only when the saved one has expired, or when you press
"Rescan".

Each price refresh makes about 3 requests for a short list (prices, your world's listings, your DC's listings for the
materials), more for long lists.

Prices refresh automatically while the window is open (every 5 minutes by default). Both the interval and the
automatic refresh itself can be changed in "Settings"; with it off, prices only load when you press Refresh or
add an item.

Notes: Universalis is crowd-sourced, so check the data age (hover "My world") and sales/day before trusting a number.

## Install (for friends)

1. In game, open `/xlsettings` -> Experimental -> Custom Plugin Repositories.
2. Paste `https://raw.githubusercontent.com/Spvvd/gilplanner/main/repo.json`, click `+` and save.
3. Open `/xlplugins`, search for "Gil Planner" and install it. Open it with `/gilplanner`.

Dalamud warns that this is a third-party repository; that is expected. After a game patch that changes Dalamud's API
level the plugin cannot load until a new version is released.

## Privacy

To load prices the plugin sends your **world name, data center name and item IDs** to
[universalis.app](https://universalis.app), together with a User-Agent that contains the plugin's version. It sends
nothing else and nothing about your character. The Popular tab scan makes about 95 requests. Prices come from
Universalis, which is built from market board data uploaded by players; please be kind to their servers.

## Releasing a new version (maintainer)

1. Make sure the build and tests pass on `main` (the "Build and test" workflow runs on every push).
2. Tag the commit with a four-number version and push the tag:

        git tag v1.0.1.0
        git push origin v1.0.1.0

3. The "Release" workflow builds and tests with that version, publishes `latest.zip` as a GitHub release and updates
   `repo.json`. Friends then get the update in `/xlplugins`.

## Build

Requires the .NET 10 SDK (on Arch/CachyOS: `sudo pacman -S dotnet-sdk`).

    dotnet build GilPlanner.slnx -c Debug
    dotnet test tests/GilPlanner.Tests

Output: `GilPlanner/bin/x64/Debug/GilPlanner.dll`. The SDK finds Dalamud in
`~/.xlcore/dalamud/Hooks/dev/`; set `DALAMUD_HOME` if yours lives elsewhere.

## Load in game

1. `/xlsettings` -> Experimental -> Dev Plugin Locations
2. Add the full path to `GilPlanner.dll`, click the `+`, then save.
3. `/xlplugins` -> Dev Tools -> Installed Dev Plugins -> enable "Gil Planner".

Rebuilding while the game runs reloads the plugin automatically if "Automatically reload dev plugins" is on.

# Docs

- `images/apps-light.png`, `images/apps-dark.png`: the Apps view in each theme, with two apps installed and one queued.
- `images/installed.png`: the inventory reported for the device.
- `images/activity.png`: the install history.
- `images/demo.png`: demo mode, which runs the whole interface from in-memory sample data with no server.
- `images/update-available.png`, `images/update-ready.png`: the two update banners, light and dark, rendered from a fabricated `update.json` against an unconfigured client.

All four were produced with `dotnet run -- --screenshot <file> <section> --theme <light|dark>` under Xvfb against the fake Action1 backend; they contain no data from a real tenant.

## Design notes

The client implements Microsoft's Windows 11 guidance rather than a generic theme:

- **Layering.** A base layer carrying navigation, and a content layer inset with an 8px top-left radius, per [layering and elevation](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering).
- **Material.** `TransparencyLevelHint="Mica, None"`. Windows 11 draws Mica behind the window and the window turns transparent; everywhere else the solid base color stands in. `MainWindow.ApplyBackdrop` handles the switch.
- **Color.** Fluent 2 tokens in `Styles/Fluent2Colors.axaml`, named after their WinUI theme resources, alpha-blended so they sit correctly on Mica. Light accent `#005FB8`, dark accent `#60CDFF`.
- **Geometry.** 4px on in-page controls, 8px on containers and cards, from [geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry).
- **Type.** Segoe UI Variable Text and Display with an Inter fallback, on the Windows type ramp: 12/16 caption, 14/20 body, 20/28 subtitle, 28/36 title. Semibold rather than bold for emphasis, sentence case throughout.
- **Navigation.** 36px items, subtle-fill hover, a 3x16 rounded accent indicator on the selected item.

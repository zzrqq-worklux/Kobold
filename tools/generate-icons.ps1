# Generates the 24px icon assets and the C# geometry catalog.
#
# Source of truth: the $icons table below. Every icon has a classic and a
# hand-drawn path in the same 24x24 coordinate space. The hand-drawn entries
# use the mockup's baked wobble paths; nothing is filtered or randomised at
# runtime.
#
# Run from the repository root:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\generate-icons.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $root 'assets\icons\24'
$catalogPath = Join-Path $root 'Core\Icons\IconCatalog.g.cs'

$order = @(
    'Settings', 'Lock', 'Pin', 'Search', 'Folder', 'File', 'Sheet', 'Doc',
    'Image', 'Plus', 'Chevron', 'Edit', 'Palette', 'Grid', 'IconSize',
    'Delete', 'Desktop', 'Close', 'Check', 'Copy', 'Refresh', 'Archive',
    'More', 'Eye', 'EyeOff', 'Globe', 'Info', 'Calendar', 'TextSize',
    'Play', 'Pause', 'Home', 'Help', 'Terminal', 'Alert'
)

$icons = @{
    'Settings' = @{
        classic = 'M12 2.8 L13.2 5.4 L15.9 4.8 L17.1 7.4 L19.9 8.1 L19.5 10.9 L21.5 12 L19.5 13.1 L19.9 15.9 L17.1 16.6 L15.9 19.2 L13.2 18.6 L12 21.2 L10.8 18.6 L8.1 19.2 L6.9 16.6 L4.1 15.9 L4.5 13.1 L2.5 12 L4.5 10.9 L4.1 8.1 L6.9 7.4 L8.1 4.8 L10.8 5.4 Z M12 8.5 A3.5 3.5 0 1 0 12 15.5 A3.5 3.5 0 1 0 12 8.5 Z'
        handdrawn = 'M12.2 4.1c2.1-.2 4 1.2 4.7 3.2.2.6.7 1 1.3 1.2 2 .6 3.2 2.5 2.7 4.5-.2.7-.2 1.3 0 1.9.6 2-.5 3.9-2.5 4.5-.6.2-1.1.6-1.4 1.2-.7 1.9-2.6 3.1-4.6 2.8-.7-.1-1.3-.1-2 0-2 .4-3.8-.9-4.3-2.9-.2-.7-.6-1.2-1.2-1.6-1.8-.9-2.7-2.9-2.1-4.8.2-.7.2-1.3 0-2-.6-1.9.3-3.8 2.2-4.6.7-.3 1.2-.8 1.5-1.4.7-1.8 2.5-2.9 4.4-2.7 M12 9.2c1.6-.1 2.9 1.2 2.9 2.8 0 1.6-1.2 2.9-2.8 2.9-1.6 0-2.9-1.2-2.9-2.8 0-1.6 1.2-2.9 2.8-2.9 M12 4.2 12 1.8M18.2 6.1 20.1 4.3M20.6 12.2 22.6 12.1M18.1 18.1 19.9 19.9M12 19.9 12.1 22.2M5.9 18.1 4.1 20M3.4 12.1 1.5 12M6 6.1 4.2 4.2'
    }
    'Lock' = @{
        classic = 'M5 11 H19 V21 H5 Z M8 11 V7 A4 4 0 0 1 16 7 V11'
        handdrawn = 'M5.3 11.1c2.2-.2 4.3-.1 6.5-.2 2.2-.1 4.4 0 6.6.2.5.1.7.5.7 1v7.2c0 .6-.4 1-1 1-2.2.2-4.4.1-6.6.1s-4.4.1-6.6-.1c-.5 0-.8-.4-.8-.9v-7.3c0-.5.3-.9.8-1Z M8.2 11V7.3c0-2.1 1.6-3.8 3.7-3.9 2.2-.1 4 1.6 4.1 3.7V11'
    }
    'Pin' = @{
        classic = 'M12 17 V22 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z'
        handdrawn = 'M12.1 16.9c-.1.8-.1 1.7-.2 2.5-.1.7-.1 1.4 0 2.1 M9.2 10.8c-.2.5-.6.9-1.1 1.1l-1.7.9c-.5.3-.8.8-.8 1.4v.7c0 .5.4.9.9.9h10.9c.5 0 .9-.4.9-.9v-.7c0-.6-.3-1.1-.8-1.4l-1.7-.9c-.5-.2-.9-.6-1.1-1.1V7.2c0-.5.4-.9.9-.9.7 0 1.2-.5 1.2-1.2S15.9 4 15.2 4H8.8c-.7 0-1.2.5-1.2 1.2s.5 1.2 1.2 1.2c.5 0 .9.4.9.9v3.5Z'
    }
    'Search' = @{
        classic = 'M11 4 A7 7 0 1 0 11 18 A7 7 0 1 0 11 4 Z M20 20 L16.5 16.5'
        handdrawn = 'M15.9 9.2c-1.9-2.2-5.2-2.4-7.3-.5-2.1 1.9-2.3 5.1-.4 7.2 1.8 2 5 2.3 7.1.5 2.1-1.8 2.5-4.9.8-7Z M14.7 14.8c1.2 1.2 2.4 2.4 3.6 3.7.4.4.4 1 0 1.4-.4.4-1 .4-1.4 0-1.2-1.2-2.4-2.4-3.5-3.7 M8.7 12.9c.1-1.4 1.1-2.4 2.5-2.6'
    }
    'Folder' = @{
        classic = 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'
        handdrawn = 'M3.3 7.1c0-.7.5-1.2 1.2-1.2h3.8c.4 0 .8.2 1 .5l1.1 1.4h7.1c.7 0 1.2.5 1.2 1.2v8.3c0 .7-.5 1.2-1.2 1.2H4.5c-.7 0-1.2-.5-1.2-1.2V7.1Z'
    }
    'File' = @{
        classic = 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6'
        handdrawn = 'M13.9 3.1c.3 0 .6.1.8.3l3.1 3.1c.2.2.3.5.3.8v11.5c0 .7-.5 1.2-1.2 1.2H7.1c-.7 0-1.2-.5-1.2-1.2V4.3c0-.7.5-1.2 1.2-1.2h6.8Z M14.1 3.2v4.2c0 .5.4.9.9.9h4.1'
    }
    'Sheet' = @{
        classic = 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M8 13h8 M8 17h8 M12 11v8'
        handdrawn = 'M13.9 3.1c.3 0 .6.1.8.3l3.1 3.1c.2.2.3.5.3.8v11.5c0 .7-.5 1.2-1.2 1.2H7.1c-.7 0-1.2-.5-1.2-1.2V4.3c0-.7.5-1.2 1.2-1.2h6.8Z M14.1 3.2v4.2c0 .5.4.9.9.9h4.1M8.2 12.9c2.5-.1 5-.1 7.5 0M8.1 16.2c2.5-.1 5-.1 7.5 0M12 10.8v7.1'
    }
    'Doc' = @{
        classic = 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M8 13h8 M8 17h6'
        handdrawn = 'M13.9 3.1c.3 0 .6.1.8.3l3.1 3.1c.2.2.3.5.3.8v11.5c0 .7-.5 1.2-1.2 1.2H7.1c-.7 0-1.2-.5-1.2-1.2V4.3c0-.7.5-1.2 1.2-1.2h6.8Z M14.1 3.2v4.2c0 .5.4.9.9.9h4.1M8.2 13.1c2.5-.1 5-.1 7.5 0M8.1 16.4c1.8-.1 3.6-.1 5.4 0'
    }
    'Image' = @{
        classic = 'M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M7 10.5 A1.5 1.5 0 1 0 10 10.5 A1.5 1.5 0 1 0 7 10.5 Z M21 15l-5-5L5 21'
        handdrawn = 'M3.4 5.2c0-.6.5-1.1 1.1-1.1h15c.6 0 1.1.5 1.1 1.1v13.6c0 .6-.5 1.1-1.1 1.1h-15c-.6 0-1.1-.5-1.1-1.1V5.2Z M4.1 17.2c2.3-2.3 4.6-4.5 7-6.6 2.3 2.2 4.6 4.4 7 6.6 M8.1 8.2c.1 1.1.9 1.9 2 1.9 1.1-.1 1.9-.9 1.9-2-.1-1.1-.9-1.9-2-1.9-1.1.1-1.9.9-1.9 2Z'
    }
    'Plus' = @{
        classic = 'M12 5 V19 M5 12 H19'
        handdrawn = 'M12.1 4.2c-.1 2.6-.1 5.2-.1 7.8 0 2.6 0 5.2.1 7.8M4.2 12.1c2.6-.1 5.2-.1 7.8-.1 2.6 0 5.2 0 7.8.1'
    }
    'Chevron' = @{
        classic = 'M9 5 L16 12 L9 19'
        handdrawn = 'M9.2 5.2c2.1 2.1 4.2 4.2 6.3 6.4.1.1.1.3 0 .4-2.1 2.2-4.2 4.3-6.2 6.5'
    }
    'Edit' = @{
        classic = 'M12 20h9 M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z'
        handdrawn = 'M4.2 19.8c2.6-.5 5.2-1 7.8-1.6M5.1 15.1 15.9 4.3c.5-.5 1.2-.5 1.7 0l2.1 2.1c.5.5.5 1.2 0 1.7L8.9 18.9l-4.2 1.1 1-4.2Z'
    }
    'Palette' = @{
        classic = 'M12 2a10 10 0 1 0 0 20c1.1 0 2-.9 2-2 0-.5-.2-1-.5-1.4-.3-.4-.5-.9-.5-1.4 0-1.1.9-2 2-2h2a4 4 0 0 0 4-4 10 10 0 0 0-9-9z M7.5 10.5 A.8 .8 0 1 0 7.5 10.51 Z M9.5 6.5 A.8 .8 0 1 0 9.5 6.51 Z M14.5 6.5 A.8 .8 0 1 0 14.5 6.51 Z M17.5 10.5 A.8 .8 0 1 0 17.5 10.51 Z'
        handdrawn = 'M12.1 3.1c-5 0-9 3.9-9 8.8 0 4.8 3.9 8.7 8.7 8.7 1.1 0 2-.9 2-2 0-.5-.2-.9-.5-1.3-.3-.4-.4-.8-.4-1.3 0-1.1.9-2 2-2h1.9c2.2 0 4-1.8 3.9-4-.1-4.8-4.1-6.9-8.6-6.9Z M7.3 10.1c.6-.1 1.1.4 1.1 1 0 .6-.5 1.1-1.1 1.1-.6 0-1.1-.5-1.1-1.1 0-.6.5-1.1 1.1-1ZM9.5 6.8c.6-.1 1.1.4 1.1 1 0 .6-.5 1.1-1.1 1.1-.6 0-1.1-.5-1.1-1.1 0-.6.5-1.1 1.1-1ZM14.5 6.9c.6-.1 1.1.4 1.1 1 0 .6-.5 1.1-1.1 1.1-.6 0-1.1-.5-1.1-1.1 0-.6.5-1.1 1.1-1ZM17.2 10.4c.6-.1 1.1.4 1.1 1 0 .6-.5 1.1-1.1 1.1-.6 0-1.1-.5-1.1-1.1 0-.6.5-1.1 1.1-1Z'
    }
    'Grid' = @{
        classic = 'M3 4 H10 V11 H3 Z M14 4 H21 V11 H14 Z M3 14 H10 V21 H3 Z M14 14 H21 V21 H14 Z'
        handdrawn = 'M3.4 3.4c2.2-.1 4.4-.1 6.6 0 .1 2.2.1 4.4 0 6.6-2.2.1-4.4.1-6.6 0-.1-2.2-.1-4.4 0-6.6ZM14 3.4c2.2-.1 4.4-.1 6.6 0 .1 2.2.1 4.4 0 6.6-2.2.1-4.4.1-6.6 0-.1-2.2-.1-4.4 0-6.6ZM3.4 14c2.2-.1 4.4-.1 6.6 0 .1 2.2.1 4.4 0 6.6-2.2.1-4.4.1-6.6 0-.1-2.2-.1-4.4 0-6.6ZM14 14c2.2-.1 4.4-.1 6.6 0 .1 2.2.1 4.4 0 6.6-2.2.1-4.4.1-6.6 0-.1-2.2-.1-4.4 0-6.6Z'
    }
    'IconSize' = @{
        classic = 'M4 20 L10 4 L16 20 M6 14 H14 M18 20 V14 M15 17 L18 14 L21 17'
        handdrawn = 'M4.1 20.1 9.8 4.2l5.6 15.9M6.2 14.8c2.4-.1 4.8-.1 7.2 0M17.9 19.9v-6.2M15.2 16.7l2.7-3 2.7 3'
    }
    'Delete' = @{
        classic = 'M3 6h18 M8 6V4h8v2 M19 6l-1 14H6L5 6 M10 11v6 M14 11v6'
        handdrawn = 'M3.2 6.1c5.9-.2 11.8-.2 17.7 0M8.2 6.1V4.2c0-.5.4-.9.9-.9h5.8c.5 0 .9.4.9.9v1.9M19.1 6.2 18 20.1H6L4.9 6.2M10.1 10.9v5.8M13.9 10.9v5.8'
    }
    'Desktop' = @{
        classic = 'M2 4 H22 V18 H2 Z M8 22 H16 M12 18 V22'
        handdrawn = 'M2.2 4.2c0-.5.4-.9.9-.9h17.8c.5 0 .9.4.9.9v13.9c0 .5-.4.9-.9.9H3.1c-.5 0-.9-.4-.9-.9V4.2Z M8.1 22.1c2.6-.1 5.2-.1 7.8 0M12 18.1v4'
    }
    'Close' = @{
        classic = 'M5 5 L19 19 M19 5 L5 19'
        handdrawn = 'M5.2 5.1 9.8 9.7 14.9 14.8 18.8 18.7M18.9 5.3 14.2 9.9 9.8 14.4 5.2 18.9'
    }
    'Check' = @{
        classic = 'M4 12.5 L9.5 18 L20 6.5'
        handdrawn = 'M4.2 12.4 6.9 15.1 9.6 17.9 12.6 14.7 16.1 11.2 19.9 6.6'
    }
    'Copy' = @{
        classic = 'M8 3 H18 Q20 3 20 5 V16 Q20 18 18 18 H16 M4 7 H14 Q16 7 16 9 V19 Q16 21 14 21 H6 Q4 21 4 19 V9 Q4 7 6 7 Z'
        handdrawn = 'M8.2 3.1H17.8Q19.9 3.2 19.8 5.2V16Q19.9 18 17.8 17.9M4.2 7.1H13.8Q15.9 7.2 15.8 9.2V18.9Q15.9 20.9 13.8 20.8H6.2Q4.1 20.9 4.2 18.9V9.2Q4.1 7.2 6.2 7.1Z'
    }
    'Refresh' = @{
        classic = 'M20 12 A8 8 0 1 1 17.5 5.7 M20 3 V8 H15'
        handdrawn = 'M19.8 12.2Q19.4 16.9 15.4 19.3Q11.4 21.6 7.2 19.6Q3.1 17.5 2.3 13.1Q1.6 8.6 5.1 5.5Q8.6 2.5 13.1 2.9M20.1 3.2 19.9 8.2 15.1 7.9'
    }
    'Archive' = @{
        classic = 'M4 7 H20 V10 H4 Z M6 10 V19 H18 V10 M10 13 H14'
        handdrawn = 'M4.2 7.1 19.8 7.2 19.9 10.1 4.1 10.0ZM6.1 10.1 6.0 18.9 17.9 19.1 18.1 10.2M10.1 13.1 13.9 13.2'
    }
    'More' = @{
        classic = 'M5.5 12 A1.5 1.5 0 1 0 8.5 12 A1.5 1.5 0 1 0 5.5 12 Z M10.5 12 A1.5 1.5 0 1 0 13.5 12 A1.5 1.5 0 1 0 10.5 12 Z M15.5 12 A1.5 1.5 0 1 0 18.5 12 A1.5 1.5 0 1 0 15.5 12 Z'
        handdrawn = 'M5.6 12.1A1.4 1.4 0 1 0 8.4 12.1A1.4 1.4 0 1 0 5.6 12.1ZM10.6 12.1A1.4 1.4 0 1 0 13.4 12.1A1.4 1.4 0 1 0 10.6 12.1ZM15.6 12.1A1.4 1.4 0 1 0 18.4 12.1A1.4 1.4 0 1 0 15.6 12.1Z'
    }
    'Eye' = @{
        classic = 'M2.5 12 Q7 5.5 12 5.5 Q17 5.5 21.5 12 Q17 18.5 12 18.5 Q7 18.5 2.5 12 Z M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z'
        handdrawn = 'M2.6 12.1Q7.2 5.7 12.1 5.6Q17.0 5.6 21.4 11.9Q17.1 18.4 12.0 18.5Q6.8 18.4 2.6 12.1ZM12.1 9.2A2.9 2.9 0 1 0 12.0 15.0A2.9 2.9 0 1 0 12.1 9.2Z'
    }
    'EyeOff' = @{
        classic = 'M2.5 12 Q7 5.5 12 5.5 Q17 5.5 21.5 12 Q17 18.5 12 18.5 Q7 18.5 2.5 12 Z M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z M4 4 L20 20'
        handdrawn = 'M2.6 12.1Q7.2 5.7 12.1 5.6Q17.0 5.6 21.4 11.9Q17.1 18.4 12.0 18.5Q6.8 18.4 2.6 12.1ZM12.1 9.2A2.9 2.9 0 1 0 12.0 15.0A2.9 2.9 0 1 0 12.1 9.2ZM4.2 4.1 8.2 8.0 12.1 11.9 16.1 15.8 19.9 19.7'
    }
    'Globe' = @{
        classic = 'M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M3 12 H21 M12 3 Q8 7.5 8 12 Q8 16.5 12 21 M12 3 Q16 7.5 16 12 Q16 16.5 12 21'
        handdrawn = 'M12.1 3.2Q16.8 3.1 19.5 6.9Q22.2 10.7 21.0 15.1Q19.8 19.5 15.6 21.0Q11.4 22.5 7.4 20.0Q3.4 17.5 2.9 12.8Q2.4 8.1 5.8 5.0Q8.5 2.8 12.1 3.2ZM3.1 12.1 20.9 12.2M12.1 3.2Q8.2 7.5 8.1 12.0Q8.1 16.6 12.0 20.9M12.1 3.2Q16.1 7.5 16.1 12.0Q16.1 16.5 12.0 20.9'
    }
    'Info' = @{
        classic = 'M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M12 10.5 V17 M12 7.5 V8'
        handdrawn = 'M12.1 3.2A8.9 8.9 0 1 0 11.9 21.0A8.9 8.9 0 1 0 12.1 3.2ZM12.0 10.6 12.1 16.9M12.0 7.5 12.1 7.9'
    }
    'Calendar' = @{
        classic = 'M4 6 H20 V20 H4 Z M4 10 H20 M8 3.5 V7 M16 3.5 V7 M8 14 H10 M12 14 H14 M16 14 H18 M8 17 H10 M12 17 H14'
        handdrawn = 'M4.2 6.1 19.8 6.0 19.9 19.8 4.1 19.9ZM4.1 10.1 19.9 10.2M8.1 3.6 8.0 6.9M16.1 3.5 16.0 7.0M8.1 13.9 9.9 14.0M12.1 13.9 13.9 14.0M16.1 13.9 17.9 14.0M8.1 16.9 9.9 17.0M12.1 16.9 13.9 17.0'
    }
    'TextSize' = @{
        classic = 'M4 19 L9 5 L14 19 M5.8 14 H12.2 M17 19 V9 M17 12 L19 13'
        handdrawn = 'M4.1 19.1 9.1 4.9 14.1 19.0M5.9 14.1 12.3 14.0M17.1 19.0 17.0 9.1M17.0 12.1 19.1 13.1'
    }
    'Play' = @{
        classic = 'M7 4 L20 12 L7 20 Z'
        handdrawn = 'M7.1 4.2 19.8 12.0 7.2 19.9Z'
    }
    'Pause' = @{
        classic = 'M8 5 V19 M16 5 V19'
        handdrawn = 'M8.1 5.1 8.0 18.9M16.1 5.0 16.0 19.0'
    }
    'Home' = @{
        classic = 'M3 11 L12 4 L21 11 M5 10 V20 H19 V10 M10 20 V14 H14 V20'
        handdrawn = 'M3.1 11.1 12.0 4.1 20.9 11.0M5.1 10.1 5.0 19.9 19.0 19.8 18.9 10.2M10.1 19.9 10.0 14.1 14.0 14.0 14.1 19.8'
    }
    'Help' = @{
        classic = 'M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M9.5 9 A2.5 2.5 0 1 1 12.5 11.5 Q12 12 12 14 M12 17 V17.5'
        handdrawn = 'M12.1 3.2A8.9 8.9 0 1 0 11.9 21.0A8.9 8.9 0 1 0 12.1 3.2ZM9.6 8.9Q9.9 6.7 12.1 6.6Q14.4 6.6 14.7 8.9Q15.0 11.2 12.6 11.6Q12.1 11.7 12.1 13.9M12.0 17.0 12.1 17.4'
    }
    'Terminal' = @{
        classic = 'M3.5 5 H20.5 V19 H3.5 Z M7 9 L10 12 L7 15 M12 15 H17'
        handdrawn = 'M3.6 5.1 20.4 5.0 20.3 18.9 3.5 19.0ZM7.1 9.1 9.9 11.9 7.2 14.9M12.1 15.1 16.9 15.0'
    }
    'Alert' = @{
        classic = 'M12 3 L22 20 H2 Z M12 9 V14 M12 17 V17.5'
        handdrawn = 'M12.1 3.2 21.8 19.9 2.2 20.0ZM12.0 9.1 11.9 14.1M12.0 16.9 12.1 17.4'
    }
}

if ($order.Count -ne 35) { throw "Expected 35 icon ids, got $($order.Count)." }
foreach ($name in $order) {
    if (-not $icons.ContainsKey($name)) { throw "Missing icon table entry: $name" }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Svg([string]$path, [string]$d) {
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
  <path d="$d"/>
</svg>
"@
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
    [System.IO.File]::WriteAllText($path, $svg, $utf8)
}

foreach ($name in $order) {
    Write-Svg (Join-Path $assetRoot "classic\$name.svg") $icons[$name].classic
    Write-Svg (Join-Path $assetRoot "handdrawn\$name.svg") $icons[$name].handdrawn
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// <auto-generated by tools/generate-icons.ps1>')
[void]$sb.AppendLine('// Do not edit by hand: edit the icon table in the generator and re-run it.')
[void]$sb.AppendLine('using System;')
[void]$sb.AppendLine('using System.Collections.Generic;')
[void]$sb.AppendLine('using System.Windows.Media;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('namespace Kobold.Core.Icons')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    /// <summary>Baked 24x24 UI icon geometries, one classic and one hand-drawn set.</summary>')
[void]$sb.AppendLine('    public static class IconCatalog')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        private static readonly object Gate = new object();')
[void]$sb.AppendLine('        private static readonly Dictionary<IconId, string> ClassicPaths = new Dictionary<IconId, string>')
[void]$sb.AppendLine('        {')
foreach ($name in $order) {
    [void]$sb.AppendLine("            { IconId.$name, `"$($icons[$name].classic)`" },")
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine()
[void]$sb.AppendLine('        private static readonly Dictionary<IconId, string> HandDrawnPaths = new Dictionary<IconId, string>')
[void]$sb.AppendLine('        {')
foreach ($name in $order) {
    [void]$sb.AppendLine("            { IconId.$name, `"$($icons[$name].handdrawn)`" },")
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine()
[void]$sb.AppendLine('        private static readonly Dictionary<IconId, Geometry> ClassicCache = new Dictionary<IconId, Geometry>();')
[void]$sb.AppendLine('        private static readonly Dictionary<IconId, Geometry> HandDrawnCache = new Dictionary<IconId, Geometry>();')
[void]$sb.AppendLine()
[void]$sb.AppendLine('        /// <summary>Every id in the catalog (used by the icon checks).</summary>')
[void]$sb.AppendLine('        public static IReadOnlyCollection<IconId> All')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            get { return (IconId[])Enum.GetValues(typeof(IconId)); }')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine()
[void]$sb.AppendLine('        /// <summary>The baked SVG path data for one icon/style.</summary>')
[void]$sb.AppendLine('        public static string GetPathData(IconId id, bool handDrawn)')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            string value;')
[void]$sb.AppendLine('            if ((handDrawn ? HandDrawnPaths : ClassicPaths).TryGetValue(id, out value)) return value;')
[void]$sb.AppendLine('            throw new ArgumentOutOfRangeException(nameof(id), id, "No icon geometry is registered.");')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine()
[void]$sb.AppendLine('        /// <summary>The parsed, frozen geometry for one icon/style.</summary>')
[void]$sb.AppendLine('        public static Geometry Get(IconId id, bool handDrawn)')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            Dictionary<IconId, Geometry> cache = handDrawn ? HandDrawnCache : ClassicCache;')
[void]$sb.AppendLine('            lock (Gate)')
[void]$sb.AppendLine('            {')
[void]$sb.AppendLine('                Geometry geometry;')
[void]$sb.AppendLine('                if (cache.TryGetValue(id, out geometry)) return geometry;')
[void]$sb.AppendLine('                geometry = Geometry.Parse(GetPathData(id, handDrawn));')
[void]$sb.AppendLine('                geometry.Freeze();')
[void]$sb.AppendLine('                cache[id] = geometry;')
[void]$sb.AppendLine('                return geometry;')
[void]$sb.AppendLine('            }')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

[System.IO.File]::WriteAllText($catalogPath, $sb.ToString(), $utf8)

Write-Host "Generated $($order.Count * 2) SVG files in $assetRoot"
Write-Host "Generated $catalogPath"

# Repository Guidelines

## Project Structure & Module Organization

The application is in `IOT_SmartParking/`. ESP32 firmware lives in `src/main.cpp`, with PlatformIO settings in `platformio.ini` and hardware wiring in `diagram.json`. The .NET 8 API is in `backend/`, organized into `Controllers/`, `Services/`, `Data/`, `Dtos/`, `Models/`, and `Configuration/`. The browser dashboard is plain HTML, CSS, and JavaScript in `dashboard/`. SQL setup and circuit/power notes are in `QL_BAIGIUXE_IOT.sql` and `docs/`. `test/` currently contains guidance rather than an automated test suite.

## Build, Test, and Development Commands

Run commands from `IOT_SmartParking/` unless noted.

- `pio run` builds the ESP32 firmware using the `esp32dev` environment.
- `pio run -t upload` builds and uploads firmware; connect the correct board and configure the port first.
- `pio device monitor` opens the serial monitor at the configured 115200 baud.
- `dotnet restore backend/SmartParking.Api.csproj` restores API dependencies.
- `dotnet run --project backend/SmartParking.Api.csproj` starts the API locally.
- `dotnet build backend/SmartParking.Api.csproj` compiles the API.

Open `dashboard/index.html` or serve `dashboard/` with a local static server to work on the UI. Configure the API, SQL Server, and MQTT settings for your environment before running integrated flows.

## Coding Style & Naming Conventions

Follow the existing language conventions: C++ firmware uses `.cpp` files and descriptive `camelCase` functions; C# uses four-space indentation, PascalCase types/methods, and the existing Vietnamese domain names; dashboard code uses readable JavaScript `camelCase` and kebab-case CSS classes. Keep hardware pins, MQTT topics, payload fields, and database schema changes synchronized across firmware, API, and SQL scripts. Avoid committing generated `backend/bin` or `backend/obj` output.

## Testing Guidelines

There is no configured automated test framework or coverage threshold. For API changes, run `dotnet build backend/SmartParking.Api.csproj` and exercise relevant requests using `backend/SmartParking.Api.http`. For firmware changes, run `pio run`; verify hardware behavior on the board or Wokwi when applicable. For dashboard changes, check the page and browser console after starting the API.

## Commit & Pull Request Guidelines

Recent commits use short, descriptive Vietnamese summaries (for example, `Thêm JWT, phân quyền RBAC, Deep Sleep, tối ưu SQL`); keep commit subjects concise and action-oriented. Pull requests should explain the behavior change, list affected components, note configuration or SQL changes, and include reproduction or validation steps. Add screenshots for visible dashboard changes and link related issues when available.

## Security & Configuration

Do not commit real passwords, tokens, broker credentials, or production connection strings. Use local development configuration or environment-specific secrets, and review both `appsettings*.json` and firmware configuration before sharing changes.

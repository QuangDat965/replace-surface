import { defineConfig, devices } from '@playwright/test'
import { resolve } from 'node:path'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  reporter: 'list',
  use: { ...devices['Desktop Chrome'], baseURL: 'http://127.0.0.1:5173' },
  webServer: [
    {
      command: 'dotnet run --project ../../src/RoomForge.Api/RoomForge.Api.csproj --no-launch-profile -- --urls http://127.0.0.1:5080',
      url: 'http://127.0.0.1:5080/health/database',
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ROOMFORGE_ASSET_ROOT: resolve(import.meta.dirname, '../../data/assets') },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 5173',
      url: 'http://127.0.0.1:5173/',
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
    },
  ],
})

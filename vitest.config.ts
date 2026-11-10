import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    projects: [{ test: { name: 'web', root: './packages/web', include: ['test/**/*.test.ts'] } }],
  },
});

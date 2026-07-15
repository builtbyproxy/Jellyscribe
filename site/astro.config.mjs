import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://jellyscribe.dev',
  trailingSlash: 'ignore',
  build: {
    assets: 'assets',
  },
});

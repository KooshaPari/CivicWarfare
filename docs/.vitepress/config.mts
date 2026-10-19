import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'CivicWarfare',
  description: 'CivicWarfare documentation',
  base: '/CivicWarfare/',
  cleanUrls: true,
  themeConfig: {
    nav: [
      { text: 'Home', link: '/' },
      { text: 'Compete', link: '/compete/' }
    ],
    sidebar: [
      { text: 'Home', link: '/' },
      { text: 'Compete', link: '/compete/' }
    ]
  }
})

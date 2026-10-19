import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'CivicSurvival Public',
  description: 'CivicSurvival public audit / governance fork',
  lang: 'en-US',
  base: '/CivicSurvival-public/',
  lastUpdated: true,
  cleanUrls: true,
  themeConfig: {
    nav: [
      { text: 'Home', link: '/' },
      { text: 'Getting Started', link: '/getting-started/' },
      { text: 'Architecture', link: '/architecture/' },
      { text: 'Reference', link: '/reference/' },
      { text: 'Operations', link: '/operations/' },
      { text: 'Demo', link: '/demo/' },
      { text: 'Compete', link: '/compete/' },
    ],
    sidebar: {
      '/compete/': [
        { text: 'Compete', items: [
          { text: 'Overview', link: '/compete/' },
        ]},
      ],
      '/getting-started/': [
        { text: 'Getting Started', items: [
          { text: 'Overview', link: '/getting-started/' },
          { text: 'Install', link: '/getting-started/install' },
          { text: 'Quickstart', link: '/getting-started/quickstart' },
          { text: 'On-device', link: '/getting-started/on-device' },
          { text: 'Deploy', link: '/getting-started/deploy' },
        ]},
      ],
      '/architecture/': [
        { text: 'Architecture', items: [
          { text: 'Overview', link: '/architecture/' },
          { text: 'ADRs', link: '/architecture/adrs' },
          { text: 'Domains', link: '/architecture/domains' },
        ]},
      ],
      '/reference/': [
        { text: 'Reference', items: [
          { text: 'Overview', link: '/reference/' },
          { text: 'Audit Log Spec', link: '/reference/audit-log' },
          { text: 'Feature Flags', link: '/reference/feature-flags' },
          { text: 'Save Format', link: '/reference/save-format' },
          { text: 'Compliance', link: '/reference/compliance' },
        ]},
      ],
      '/operations/': [
        { text: 'Operations', items: [
          { text: 'Overview', link: '/operations/' },
          { text: 'Runbook', link: '/operations/runbook' },
          { text: 'Incident Response', link: '/operations/incident-response' },
          { text: 'Disaster Recovery', link: '/operations/disaster-recovery' },
          { text: 'Analytics', link: '/operations/analytics' },
          { text: 'Feedback', link: '/operations/feedback' },
          { text: 'Sessions', link: '/operations/sessions' },
        ]},
      ],
      '/demo/': [
        { text: 'Demo', items: [
          { text: 'Overview', link: '/demo/' },
          { text: 'GUI Walkthrough', link: '/demo/gui' },
          { text: 'Stress Test', link: '/demo/stress-test' },
        ]},
      ],
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/KooshaPari/CivicSurvival-public' },
    ],
    footer: {
      message: 'Fork of Theorist100/CivicSurvival-public — Phenotype governance applies.',
      copyright: 'Copyright © KooshaPari/CivicSurvival-public',
    },
  },
  vite: {
    server: { host: '127.0.0.1', port: 5174 },
  },
})

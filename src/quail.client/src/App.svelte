<script lang="ts">
  import { api } from './lib/api';
  import { getAuth, getRouter } from './lib/stores/state.svelte';
  import Login from './lib/components/Login.svelte';
  import Register from './lib/components/Register.svelte';
  import Sidebar from './lib/components/Sidebar.svelte';
  import Inbox from './lib/components/Inbox.svelte';
  import EmailView from './lib/components/EmailView.svelte';
  import Compose from './lib/components/Compose.svelte';

  const auth = getAuth();
  const router = getRouter();

  // Check if already logged in
  $effect(() => {
    api.auth.me().then(user => {
      auth.user = user;
      router.route = 'inbox';
    }).catch(() => {});
  });

  function toggleSidebar() {
    router.sidebarOpen = !router.sidebarOpen;
  }

  function closeSidebar() {
    router.sidebarOpen = false;
  }
</script>

{#if !auth.isLoggedIn}
  {#if router.route === 'register'}
    <Register />
  {:else}
    <Login />
  {/if}
{:else}
  <div class="app-layout">
    <!-- Mobile header -->
    <header class="mobile-header">
      <button class="menu-btn" onclick={toggleSidebar} aria-label="Toggle menu">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <line x1="3" y1="12" x2="21" y2="12"/><line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="18" x2="21" y2="18"/>
        </svg>
      </button>
      <span class="mobile-title">
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="var(--color-primary)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M22 2L2 8.5l7.5 3L22 2z"/><path d="M22 2l-8.5 17.5-3-7.5L22 2z"/><path d="M9.5 11.5l3 7.5"/>
        </svg>
        Quail
      </span>
    </header>

    <!-- Sidebar overlay for mobile -->
    {#if router.sidebarOpen}
      <div class="sidebar-overlay" onclick={closeSidebar} role="presentation"></div>
    {/if}

    <div class="sidebar-wrapper" class:open={router.sidebarOpen}>
      <Sidebar />
    </div>
    <main>
      {#if router.route === 'email' && router.selectedEmailId}
        <EmailView />
      {:else if router.route === 'compose'}
        <Compose />
      {:else}
        <Inbox />
      {/if}
    </main>
  </div>
{/if}

<style>
  .app-layout {
    display: flex;
    height: 100vh;
    width: 100vw;
    position: relative;
  }
  .mobile-header {
    display: none;
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    height: 52px;
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);
    align-items: center;
    padding: 0 1rem;
    gap: 0.75rem;
    z-index: 100;
  }
  .menu-btn {
    background: none;
    border: none;
    cursor: pointer;
    padding: 0.4rem;
    border-radius: var(--radius-sm);
    color: var(--color-text);
    transition: background var(--transition);
  }
  .menu-btn:hover { background: var(--color-border-light); }
  .mobile-title {
    font-weight: 700;
    font-size: 1rem;
    color: var(--color-text);
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }
  .sidebar-overlay {
    display: none;
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.4);
    z-index: 200;
    backdrop-filter: blur(2px);
  }
  .sidebar-wrapper {
    flex-shrink: 0;
  }
  main {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    min-width: 0;
  }

  @media (max-width: 768px) {
    .mobile-header {
      display: flex;
    }
    .app-layout {
      flex-direction: column;
      padding-top: 52px;
    }
    .sidebar-wrapper {
      position: fixed;
      top: 0;
      left: 0;
      bottom: 0;
      z-index: 300;
      transform: translateX(-100%);
      transition: transform 0.25s ease;
    }
    .sidebar-wrapper.open {
      transform: translateX(0);
    }
    .sidebar-overlay {
      display: block;
    }
    main {
      width: 100%;
    }
  }
</style>

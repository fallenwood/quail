<script lang="ts">
  import { api } from '../api';
  import { getAuth, getRouter } from '../stores/state.svelte';

  const auth = getAuth();
  const router = getRouter();

  let username = $state('');
  let email = $state('');
  let password = $state('');
  let confirmPassword = $state('');
  let error = $state('');
  let loading = $state(false);

  async function handleRegister() {
    error = '';
    if (password !== confirmPassword) {
      error = 'Passwords do not match';
      return;
    }
    loading = true;
    try {
      await api.auth.register(username, email, password);
      // Auto-login after registration
      const user = await api.auth.login(username, password);
      auth.user = user;
      router.route = 'inbox';
    } catch (e: any) {
      error = e.message || 'Registration failed';
    } finally {
      loading = false;
    }
  }
</script>

<div class="auth-page">
  <div class="auth-card">
    <div class="brand">
      <svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="brand-icon">
        <path d="M22 2L2 8.5l7.5 3L22 2z"/>
        <path d="M22 2l-8.5 17.5-3-7.5L22 2z"/>
        <path d="M9.5 11.5l3 7.5"/>
      </svg>
      <h1>Quail Mail</h1>
    </div>
    <p class="subtitle">Create your account</p>

    {#if error}
      <div class="error">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/><line x1="9" y1="9" x2="15" y2="15"/>
        </svg>
        {error}
      </div>
    {/if}

    <form onsubmit={(e) => { e.preventDefault(); handleRegister(); }}>
      <div class="field">
        <label for="reg-username">Username</label>
        <input id="reg-username" type="text" bind:value={username} required autocomplete="username" />
      </div>
      <div class="field">
        <label for="reg-email">Email</label>
        <input id="reg-email" type="email" bind:value={email} required placeholder="you@example.com" autocomplete="email" />
      </div>
      <div class="field">
        <label for="reg-password">Password</label>
        <input id="reg-password" type="password" bind:value={password} required minlength="6" autocomplete="new-password" />
      </div>
      <div class="field">
        <label for="reg-confirm">Confirm Password</label>
        <input id="reg-confirm" type="password" bind:value={confirmPassword} required autocomplete="new-password" />
      </div>
      <button type="submit" class="submit-btn" disabled={loading}>
        {#if loading}
          <svg class="spinner" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M12 2v4m0 12v4m-7.07-3.93l2.83-2.83m8.48-8.48l2.83-2.83M2 12h4m12 0h4m-3.93 7.07l-2.83-2.83M6.34 6.34L3.51 3.51"/>
          </svg>
          Creating account...
        {:else}
          Create Account
        {/if}
      </button>
    </form>

    <p class="switch">
      Already have an account?
      <button class="link" onclick={() => router.route = 'login'}>Sign in</button>
    </p>
  </div>
</div>

<style>
  .auth-page {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    padding: 1.5rem;
    background: var(--color-bg);
  }
  .auth-card {
    width: 100%;
    max-width: 380px;
    padding: 2.5rem;
    border-radius: var(--radius-lg);
    box-shadow: var(--shadow-lg);
    background: var(--color-surface);
    border: 1px solid var(--color-border-light);
  }
  .brand {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.6rem;
    margin-bottom: 0.25rem;
  }
  .brand-icon { color: var(--color-primary); }
  .brand h1 {
    font-size: 1.4rem;
    font-weight: 700;
    margin: 0;
  }
  .subtitle {
    text-align: center;
    color: var(--color-text-muted);
    font-size: 0.9rem;
    margin-bottom: 2rem;
  }
  .field { margin-bottom: 1.25rem; }
  label {
    display: block;
    margin-bottom: 0.35rem;
    font-weight: 500;
    font-size: 0.8rem;
    color: var(--color-text-secondary);
    text-transform: uppercase;
    letter-spacing: 0.03em;
  }
  input {
    display: block;
    width: 100%;
    padding: 0.65rem 0.85rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    font-size: 0.95rem;
    font-family: inherit;
    color: var(--color-text);
    transition: border-color var(--transition), box-shadow var(--transition);
  }
  input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(79, 70, 229, 0.1);
  }
  input::placeholder { color: var(--color-text-muted); }
  .submit-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.4rem;
    width: 100%;
    padding: 0.75rem;
    margin-top: 0.5rem;
    background: var(--color-primary);
    color: white;
    border: none;
    border-radius: var(--radius-sm);
    font-size: 0.95rem;
    font-weight: 600;
    font-family: inherit;
    cursor: pointer;
    transition: background var(--transition), box-shadow var(--transition);
  }
  .submit-btn:hover { background: var(--color-primary-hover); box-shadow: 0 4px 12px rgba(79, 70, 229, 0.3); }
  .submit-btn:disabled { opacity: 0.6; cursor: default; box-shadow: none; }
  .spinner { animation: spin 1s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  .error {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: var(--color-danger-light);
    color: var(--color-danger);
    padding: 0.75rem 1rem;
    border-radius: var(--radius-sm);
    margin-bottom: 1.25rem;
    font-size: 0.85rem;
    border: 1px solid rgba(239, 68, 68, 0.2);
  }
  .switch { text-align: center; margin-top: 1.5rem; font-size: 0.85rem; color: var(--color-text-muted); }
  .link {
    background: none;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    font-weight: 500;
    font-family: inherit;
    font-size: inherit;
  }
  .link:hover { text-decoration: underline; }

  @media (max-width: 480px) {
    .auth-card { padding: 1.75rem; }
  }
</style>

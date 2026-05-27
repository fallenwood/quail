import type { User } from '../api';

export interface ComposeContext {
  to: string;
  cc: string;
  bcc: string;
  subject: string;
  body: string;
}

let currentUser = $state<User | null>(null);
let currentRoute = $state<string>('login');
let selectedMailboxId = $state<number | null>(null);
let selectedMailboxSpecialUse = $state<string | null>(null);
let selectedEmailId = $state<number | null>(null);
let sidebarOpen = $state(false);
let composeContext = $state<ComposeContext | null>(null);

export function getAuth() {
  return {
    get user() { return currentUser; },
    set user(v: User | null) { currentUser = v; },
    get isLoggedIn() { return currentUser !== null; },
  };
}

export function getRouter() {
  return {
    get route() { return currentRoute; },
    set route(v: string) { currentRoute = v; },
    get selectedMailboxId() { return selectedMailboxId; },
    set selectedMailboxId(v: number | null) { selectedMailboxId = v; },
    get selectedMailboxSpecialUse() { return selectedMailboxSpecialUse; },
    set selectedMailboxSpecialUse(v: string | null) { selectedMailboxSpecialUse = v; },
    get selectedEmailId() { return selectedEmailId; },
    set selectedEmailId(v: number | null) { selectedEmailId = v; },
    get sidebarOpen() { return sidebarOpen; },
    set sidebarOpen(v: boolean) { sidebarOpen = v; },
    get composeContext() { return composeContext; },
    set composeContext(v: ComposeContext | null) { composeContext = v; },
  };
}

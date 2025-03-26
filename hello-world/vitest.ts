import { 
    describe as vitestDescribe, 
    test as vitestTest, 
    expect as vitestExpect 
} from 'vitest';

// Redirect Jest functions to Vitest
globalThis.describe = vitestDescribe;
globalThis.test = vitestTest;
globalThis.it = vitestTest; // Alias for `it` if used in tests
globalThis.expect = vitestExpect;
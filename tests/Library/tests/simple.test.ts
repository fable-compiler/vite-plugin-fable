import { describe, it, expect } from 'vitest';
import * as Libary from '../src/Library/index.fs';

console.log("HERE")
console.log(Libary)
console.log("STOP")

describe('add function', () => {
  it('should return the sum of two numbers', () => { 
    expect(Libary.add(2, 3)).toBe(5);
    expect(Libary.add(-1, 1)).toBe(0);
  });

  it('should handle zero', () => {
    expect(Libary.add(0, 0)).toBe(0);
    expect(Libary.add(5, 0)).toBe(5);
  });
});

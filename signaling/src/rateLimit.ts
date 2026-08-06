/** Token-Bucket pro Verbindung. Verhindert, dass ein Client den Server flutet. */
export class TokenBucket {
  private tokens: number;
  private lastRefill: number;

  constructor(
    private readonly ratePerSec: number,
    private readonly burst: number,
    now: number = Date.now(),
  ) {
    this.tokens = burst;
    this.lastRefill = now;
  }

  /** true = erlaubt (ein Token verbraucht), false = Limit erreicht. */
  tryConsume(now: number = Date.now()): boolean {
    const elapsedSec = (now - this.lastRefill) / 1000;
    if (elapsedSec > 0) {
      this.tokens = Math.min(this.burst, this.tokens + elapsedSec * this.ratePerSec);
      this.lastRefill = now;
    }
    if (this.tokens < 1) return false;
    this.tokens -= 1;
    return true;
  }

  /** Nur fuer Tests/Diagnose. */
  get available(): number { return this.tokens; }
}

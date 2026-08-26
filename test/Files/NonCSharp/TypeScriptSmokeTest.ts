import { Component } from "framework";

export namespace CodeNav.SmokeTest {

    export interface IWorker {
        name: string;
        readonly maximum?: number;
        execute(value: number): void;
    }

    export abstract class WorkerBase implements IWorker {
        protected _first: number;
        protected _second: number;

        public static readonly maximum: number = 10;

        name: string = "default";

        constructor(value: number) {
            this._first = value;
        }

        abstract run(): void;

        get total(): number {
            return this._first + this._second;
        }

        set total(value: number) {
            this._second = value - this._first;
        }
    }

    export class SampleWorker extends WorkerBase {
        private readonly _cache = new Map<string, { count: number }>();

        // #region Worker members
        // #region Execution
        execute(value: number): void {
            this._second = value;
        }

        calculate(value: number): string {
            return (value + this._second).toString();
        }
        // #endregion

        run(): void {
            this.execute(this._first);
        }
        // #endregion

        async fetchData(id: number, opts?: { retries: number }): Promise<string> {
            const url = `/api/${id}`;
            if (opts && opts.retries > 3) {
                throw new Error("too many retries");
            }
            return url;
        }
    }

    export const createWorker = (value: number): SampleWorker => {
        return new SampleWorker(value);
    };

    export const add = (a: number, b: number): number => a + b;

    export enum WorkState {
        NotStarted,
        Running,
        Complete = "COMPLETE",
    }

    export type Transformer = (value: number) => string;

    // #region Helpers
    function helper(x: number): number {
        return x * 2;
    }

    const HELPER_CONSTANT = 42;
    // #endregion
}

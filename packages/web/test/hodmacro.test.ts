// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest';
import {
  hodString,
  hodText,
  holdsPassword,
  parseHodMacro,
  writeHodMacro,
  type HodMacro,
} from '../src/hodmacro.ts';

/** A macro as ACS saves one: placeholder screens and text only. */
const SAMPLE = `<HAScript name="Placeholder list" description="Opens the list" timeout="60000" pausetime="300" promptall="true" blockinput="false" author="" creationdate="" supressclearevents="false" usevars="false" ignorepauseforenhancedtn="true" delayifnotenhancedtn="0" ignorepausetimeforenhancedtn="true" continueontimeout="false">
    <screen name="Screen1" entryscreen="true" exitscreen="false" transient="false">
        <description>
            <oia status="NOTINHIBITED" optional="false" invertmatch="false" />
            <string value="'IBM i Main Menu'" row="1" col="1" erow="1" ecol="80" casesense="false" wrap="false" optional="false" invertmatch="false" />
            <cursor row="20" col="7" optional="true" invertmatch="false" />
        </description>
        <actions>
            <input value="'4[enter]'" row="0" col="0" movecursor="true" xlatehostkeys="true" encrypted="false" />
        </actions>
        <nextscreens timeout="0">
            <nextscreen name="Screen2" />
        </nextscreens>
    </screen>
    <screen name="Screen2" entryscreen="false" exitscreen="true" transient="false">
        <description>
            <oia status="NOTINHIBITED" optional="false" invertmatch="false" />
            <numinputfields number="5" optional="false" invertmatch="false" />
        </description>
        <actions>
            <prompt name="'Item'" description="" row="7" col="3" len="1" default="5" clearfield="false" encrypted="false" movecursor="true" xlatehostkeys="true" assigntovar="" varupdateonly="false" />
            <pause value="500" />
            <extract name="'Title'" planetype="TEXT_PLANE" srow="1" scol="1" erow="1" ecol="80" unwrap="false" continuous="false" />
        </actions>
        <nextscreens timeout="0">
        </nextscreens>
    </screen>
</HAScript>
`;

describe('Host-on-Demand macros', () => {
  it('reads screens, how each is recognised, what to do there and what comes next', () => {
    const macro = parseHodMacro(SAMPLE) as HodMacro;
    expect(macro.name).toBe('Placeholder list');
    expect(macro.description).toBe('Opens the list');
    expect(macro.attributes.promptall).toBe('true');
    const [first, second] = macro.screens;
    expect(first).toMatchObject({ name: 'Screen1', entry: true, exit: false, next: ['Screen2'] });
    expect(first.description[1]).toMatchObject({
      kind: 'string',
      value: "'IBM i Main Menu'",
      row: 1,
      ecol: 80,
    });
    expect(first.description[2]).toMatchObject({ kind: 'cursor', optional: true });
    expect(first.actions[0]).toMatchObject({
      kind: 'input',
      value: "'4[enter]'",
      encrypted: false,
    });
    expect(second.actions.map((a) => a.kind)).toEqual(['prompt', 'pause', 'other']);
    expect(holdsPassword(macro)).toBe(false);
  });

  it('writes a macro back without losing what it had', () => {
    const macro = parseHodMacro(SAMPLE) as HodMacro;
    const again = parseHodMacro(writeHodMacro(macro)) as HodMacro;
    expect(again).toEqual(macro);
  });

  it('says what is wrong with a file that isn’t a macro', () => {
    expect(parseHodMacro('<nope')).toBe('This file isn’t well-formed XML.');
    expect(parseHodMacro('<macro/>')).toBe('This isn’t a Host-on-Demand macro (no HAScript).');
  });

  it('notices a stored password', () => {
    const withPassword = SAMPLE.replace(
      `<input value="'4[enter]'" row="0" col="0" movecursor="true" xlatehostkeys="true" encrypted="false" />`,
      `<input value="c2VjcmV0" row="7" col="53" movecursor="true" xlatehostkeys="true" encrypted="true" />`,
    );
    expect(holdsPassword(parseHodMacro(withPassword) as HodMacro)).toBe(true);
  });

  it('reads and writes HOD text', () => {
    expect(hodText("'it''s'")).toBe("it's");
    expect(hodString("it's")).toBe("'it''s'");
  });
});

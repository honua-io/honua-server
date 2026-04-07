import { resolve } from 'node:path';
import { bootstrapHonuaServer } from '../shared/js-bootstrap.js';

const projectRoot = resolve(__dirname, '..', '..');

export default async function () {
  const { teardown } = await bootstrapHonuaServer({
    defaultPort: '5555',
    label: 'JS tests',
    projectRoot,
  });
  return teardown;
}

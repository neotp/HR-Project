import fs from 'node:fs/promises';
import { FileBlob, SpreadsheetFile } from '@oai/artifact-tool';

const source = 'C:/Users/chaitanachote/Downloads/EmployeeMigrationTemplate.xlsx';
const workbook = await SpreadsheetFile.importXlsx(await FileBlob.load(source));
const sheet = workbook.worksheets.getItemAt(0);
const guide = workbook.worksheets.getItemAt(1);
const header = sheet.getRange('A1:FT1').values[0];
console.log('SHEETS', sheet.name, guide.name);
console.log('COLUMNS', header.length);
console.log('TARGETS', header.map((value, index) => ({index, value})).filter(x => /รหัสบริษัท|ฝ่าย|ส่วนงาน|รหัสตำแหน่ง/.test(String(x.value))));
console.log('GUIDE_MATCHES', guide.getRange('A19:E195').values.map((row, index) => ({row: index + 19, values: row})).filter(x => /รหัสบริษัท|ฝ่าย|ส่วนงาน|รหัสตำแหน่ง/.test(JSON.stringify(x.values))));
console.log('TOP_GUIDE', guide.getRange('A1:E18').values);
console.log('DATA_ROWS', sheet.getRange('A2:FT3').values.map(row => row.filter(x => x !== null && x !== '').length));
console.log('VALIDATIONS', sheet.dataValidations.items.map(x => ({range:x.range, rule:x.rule})));
console.log('HELP_DELETE', workbook.help('delete columns rows', {search:'delete|remove', include:'index,examples,notes', maxChars:4000}).ndjson);
const preview = await workbook.render({sheetName:sheet.name, range:'AP1:AZ6', scale:1.5, format:'png'});
await fs.writeFile('C:/project reference/HR-Project/.artifact-work/employee-template-fix/before.png', new Uint8Array(await preview.arrayBuffer()));

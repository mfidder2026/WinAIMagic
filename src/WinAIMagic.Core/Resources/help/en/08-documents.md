---
title: Documents
icon: Document
keywords: Word docx project attachments house style draft sources cover page table of contents
---
Make Word documents without a cloud AI. A **project** is a folder with:
- **attachments**: context and information, such as requirements, examples and notes;
- a **house style**: only the layout (a .docx);
- the **documents** you make.

## How it works
1. Make a project and add attachments and a house style.
2. Ask for example: "make a project plan". The model looks in the attachments for what the document should contain, and searches the web for what the attachments do not say.
3. You get a **draft in Markdown** with numbered sources. There you change all text.
4. Last, the app makes the **.docx** in your house style: cover page, headers and footers, numbered headings, tables and a table of contents.

What goes on the cover page you fill in once per project. The documents are in `Documents\WinAIMagic`.

> A fast model does the small work (notes per page, spelling) and a heavy model the writing. Choose them under Models with **Fast** and **Heavy**. The fast model runs on the processor, beside the heavy one on the graphics card.

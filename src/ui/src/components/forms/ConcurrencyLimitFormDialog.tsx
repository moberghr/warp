import { FormDialog, type FormDialogField } from './FormDialog';
import {
  concurrencyLimitSchema,
  type ConcurrencyLimitFormValues,
} from '@/lib/schemas/concurrencyLimit';

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  mode: 'create' | 'edit';
  initial?: ConcurrencyLimitFormValues;
  existingNames?: Set<string>;
  onSubmit: (values: ConcurrencyLimitFormValues) => Promise<void>;
};

const defaultValues: ConcurrencyLimitFormValues = {
  name: '',
  limit: 5,
};

const fields: FormDialogField<ConcurrencyLimitFormValues>[] = [
  { name: 'name', label: 'Name', placeholder: 'e.g. payment-api', className: 'font-mono', disabledOnEdit: true, autoFocusOnCreate: true },
  { name: 'limit', label: 'Limit', type: 'number', min: 1, autoFocusOnEdit: true },
];

export function ConcurrencyLimitFormDialog({ open, onOpenChange, mode, initial, existingNames, onSubmit }: Props) {
  return (
    <FormDialog
      open={open}
      onOpenChange={onOpenChange}
      mode={mode}
      title={{ create: 'Add concurrency limit', edit: 'Edit concurrency limit' }}
      description={<>Runtime override for a <code className="font-mono">[Mutex]</code> or <code className="font-mono">[Semaphore]</code> key. Takes effect on next pickup.</>}
      schema={concurrencyLimitSchema}
      fields={fields}
      defaultValues={defaultValues}
      initial={initial}
      existingNames={existingNames}
      nameField="name"
      duplicateMessage="A limit with that name already exists"
      successMessage={{ create: 'Concurrency limit added', edit: 'Concurrency limit updated' }}
      onSubmit={onSubmit}
    />
  );
}

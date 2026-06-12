import { FormDialog, type FormDialogField } from './FormDialog';
import { rateLimitSchema, type RateLimitFormValues } from '@/lib/schemas/rateLimit';

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  mode: 'create' | 'edit';
  initial?: RateLimitFormValues;
  existingNames?: Set<string>;
  onSubmit: (values: RateLimitFormValues) => Promise<void>;
};

const defaultValues: RateLimitFormValues = {
  name: '',
  count: 100,
  windowSeconds: 60,
};

const fields: FormDialogField<RateLimitFormValues>[] = [
  { name: 'name', label: 'Name', placeholder: 'e.g. external-api', className: 'font-mono', disabledOnEdit: true, autoFocusOnCreate: true },
  { name: 'count', label: 'Count', type: 'number', min: 1, autoFocusOnEdit: true },
  { name: 'windowSeconds', label: 'Window (seconds)', type: 'number', min: 1 },
];

export function RateLimitFormDialog({ open, onOpenChange, mode, initial, existingNames, onSubmit }: Props) {
  return (
    <FormDialog
      open={open}
      onOpenChange={onOpenChange}
      mode={mode}
      title={{ create: 'Add rate limit', edit: 'Edit rate limit' }}
      description={<>Runtime override for a <code className="font-mono">[RateLimit]</code> key. Takes effect on next pickup.</>}
      schema={rateLimitSchema}
      fields={fields}
      defaultValues={defaultValues}
      initial={initial}
      existingNames={existingNames}
      nameField="name"
      duplicateMessage="A rate limit with that name already exists"
      successMessage={{ create: 'Rate limit added', edit: 'Rate limit updated' }}
      onSubmit={onSubmit}
    />
  );
}
